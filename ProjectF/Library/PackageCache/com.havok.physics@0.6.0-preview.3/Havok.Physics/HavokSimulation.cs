using System;
using System.Runtime.InteropServices;
using Unity.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Systems;

namespace Havok.Physics
{
    // Registers the Havok simulation as an option in StepPhysicsWorld
    // TODO: Is there a better way to do this registration?
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [AlwaysUpdateSystem]
    public class RegisterHavok : SystemBase
    {
        protected override void OnUpdate()
        {
            World.GetExistingSystem<StepPhysicsWorld>().RegisterSimulation(SimulationType.HavokPhysics, () =>
            {
                HavokConfiguration config = HasSingleton<HavokConfiguration>() ? GetSingleton<HavokConfiguration>() : HavokConfiguration.Default;
                var simulation = new HavokSimulation(config);
                simulation.SetStaticBodiesChangedFlag(World.GetExistingSystem<BuildPhysicsWorld>().HaveStaticBodiesChanged);
                return simulation;
            });
        }
    }

    public struct SimulationContext : IDisposable
    {
        internal float TimeStep;
        internal NativeArray<Velocity> InputVelocities;

        [NativeDisableUnsafePtrRestriction]
        internal readonly unsafe HavokSimulation.StepContext* StepContext;
        internal HavokSimulation.Camera Camera;

        internal readonly int WorldIndex;
        internal readonly bool VisualDebuggerEnabled;

        // Internal flag used only once to force static bodies to sync, even if they haven't changed.
        // This is used in the first frame of simulation, in order to properly set up data on C++ side
        internal bool StaticBodiesSyncedOnce;

        /// <summary>
        /// This array of size 1 optimizes the speed of static body synchronization.
        /// If HaveStaticBodiesChanged[0] == 1, the static body sync will happen, otherwise it won't
        /// </summary>
        // [NativeDisableContainerSafetyRestriction] is used because HaveStaticBodiesChanged can have default value,
        // and is just a workaround for not allocating and managing the array that we would have to create.
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<int> HaveStaticBodiesChanged;

        /// <summary>
        /// Denotes that the world with WorldIndex was allocated (in the constructor) and WorldIndex is not 0 just by default
        /// </summary>
        private readonly bool m_WorldAllocated;

        public SimulationContext(HavokConfiguration config)
        {
            // Unlock the plugin if it hasn't already been done.
            // If it remains locked, the simulation will do nothing.
            Plugin.EnsureUnlocked();

            TimeStep = default;
            InputVelocities = default;

            Camera = default;

            unsafe
            {
                // Allocate this at a fixed memory location. The plugin writes to it.
                StepContext = (HavokSimulation.StepContext*)UnsafeUtility.Malloc(sizeof(HavokSimulation.StepContext), 16, Allocator.Persistent);
                UnsafeUtility.MemClear(StepContext, sizeof(HavokSimulation.StepContext));

                VisualDebuggerEnabled = config.VisualDebugger.Enable != 0;
                WorldIndex = Plugin.HP_AllocateWorld(ref config, StepContext);
                m_WorldAllocated = true;
            }

            HaveStaticBodiesChanged = default;
            StaticBodiesSyncedOnce = false;
        }

        public void Reset(ref PhysicsWorld world, UnityEngine.Camera camera = null)
        {
            int numDynamicBodies = world.NumDynamicBodies;
            if (!InputVelocities.IsCreated || InputVelocities.Length < numDynamicBodies)
            {
                if (InputVelocities.IsCreated)
                {
                    InputVelocities.Dispose();
                }
                InputVelocities = new NativeArray<Velocity>(numDynamicBodies, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            if (VisualDebuggerEnabled)
            {
                camera = (camera == null ? UnityEngine.Camera.main : camera);
                Camera = new HavokSimulation.Camera
                {
                    From = camera.transform.position,
                    To = camera.transform.position + camera.transform.forward,
                    Up = camera.transform.up,
                    NearPlane = camera.nearClipPlane,
                    FarPlane = camera.farClipPlane,
                    FieldOfView = camera.fieldOfView
                };
            }
        }

        public void Dispose()
        {
            if (m_WorldAllocated)
            {
                // Destroy world only if it was allocated
                Plugin.HP_DestroyWorld(WorldIndex);
            }

            if (InputVelocities.IsCreated)
            {
                InputVelocities.Dispose();
            }

            unsafe
            {
                UnsafeUtility.Free(StepContext, Allocator.Persistent);
            }
        }
    }

    // Phases of Havok simulation. Please keep in sync with callbacks (SimulationCallbacks.Phase), there should be at least 1 phase before and after each callback.
    internal enum SimulationPhase
    {
        CreateDispatchPairs,
        CreateContacts,
        CreateContactJacobians,
        SolveJacobians,
        SolveSecondaryBodies // Not used, since secondary bodies are not supported yet.
    }

    // Steps a physics world using Havok Physics plugin
    public class HavokSimulation : ISimulation
    {
        public SimulationType Type => SimulationType.HavokPhysics;
        public JobHandle FinalSimulationJobHandle => m_StepHandles.FinalExecutionHandle;
        public JobHandle FinalJobHandle => JobHandle.CombineDependencies(FinalSimulationJobHandle, m_StepHandles.FinalDisposeHandle);

        private SimulationContext m_SimulationContext;
        private SimulationJobHandles m_StepHandles = new SimulationJobHandles(new JobHandle());

        private int WorldIndex => m_SimulationContext.WorldIndex;
        private bool VisualDebuggerEnabled => m_SimulationContext.VisualDebuggerEnabled;
        private unsafe StepContext* PhysicsStepContext => m_SimulationContext.StepContext;

        internal unsafe HpIntArray* PluginIndexToLocal => PhysicsStepContext->pluginBodyIndexToLocal;
        internal unsafe HpBlockStream* NewBodyPairsStream => PhysicsStepContext->newBroadphasePairs;
        internal unsafe HpBlockStream* ManifoldStream => PhysicsStepContext->manifoldStream;
        internal unsafe HpLinkedRange* CollisionEventsRange => PhysicsStepContext->collisionEventsRange;
        internal unsafe HpLinkedRange* TriggerEventsRange => PhysicsStepContext->triggerEventsRange;
        internal unsafe HpGrid* FixedJacobianGrid => PhysicsStepContext->jacFixedGrid;
        internal unsafe HpGrid* MovingJacobianGrid => PhysicsStepContext->jacMovingGrid;
        internal float TimeStep => m_SimulationContext.TimeStep;

        public unsafe HavokCollisionEvents CollisionEvents => new HavokCollisionEvents(CollisionEventsRange, m_SimulationContext.InputVelocities, TimeStep);
        public unsafe HavokTriggerEvents TriggerEvents => new HavokTriggerEvents(TriggerEventsRange);

        /// <summary>
        /// Sets the HaveStaticBodiesChanged in HavokSimulation.SimulationContext.
        /// See HavokSimulation.SimulationContext.HaveStaticBodiesChanged for detailed explanation.
        /// </summary>
        public void SetStaticBodiesChangedFlag(NativeArray<int> haveStaticBodiesChanged)
        {
            Assert.IsTrue(haveStaticBodiesChanged.Length == 1);
            m_SimulationContext.HaveStaticBodiesChanged = haveStaticBodiesChanged;
        }

        // Input parameters for HP_StepWorld()
        [StructLayout(LayoutKind.Sequential)]
        internal struct StepInput
        {
            public float m_timeStep;
            public float3 m_gravity;
            public int m_numThreads;
            public int m_numSolverIterations;
        }

        // Context written to by HP_AllocateWorld() and HP_StepWorld()
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct StepContext
        {
            public Task* broadphase;
            public Task* narrowphase;
            public Task* solverPrep;
            public Task* solverSolve;
            public Task* particles;

            public HpBlockStream* newBroadphasePairs;
            public HpBlockStream* manifoldStream;
            public HpGrid* jacFixedGrid;
            public HpGrid* jacMovingGrid;

            public HpLinkedRange* collisionEventsRange;
            public HpLinkedRange* triggerEventsRange;

            public HpIntArray* pluginBodyIndexToLocal;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Task
        {
            public IntPtr m_task;
            public int m_multiplicity;
            public int m_runCount;
            public int m_worldIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Camera
        {
            public float3 From;
            public float3 To;
            public float3 Up;
            public float NearPlane;
            public float FarPlane;
            public float FieldOfView;
        }

        public HavokSimulation(HavokConfiguration config)
        {
            m_SimulationContext = new SimulationContext(config);
        }

        public void Dispose()
        {
            m_SimulationContext.Dispose();
        }

        public static void StepImmediate(SimulationStepInput input, ref SimulationContext simulationContext)
        {
            ref PhysicsWorld world = ref input.World;

            simulationContext.TimeStep = input.TimeStep;

            // Early out if there will be no effective result
            if (world.NumDynamicBodies == 0 && !simulationContext.VisualDebuggerEnabled)
            {
                return;
            }

            bool syncStaticBodies = !simulationContext.StaticBodiesSyncedOnce || simulationContext.HaveStaticBodiesChanged == default
                || simulationContext.HaveStaticBodiesChanged[0] == 1;

            // Sync the world
            unsafe
            {
                var bodies = world.Bodies.GetUnsafeReadOnlyPtr();
                var motionDatas = (MotionData*)world.MotionDatas.GetUnsafeReadOnlyPtr();
                var motionVelocities = (MotionVelocity*)world.MotionVelocities.GetUnsafeReadOnlyPtr();
                var joints = world.Joints.GetUnsafeReadOnlyPtr();

                Plugin.HP_SyncWorldIn(
                    simulationContext.WorldIndex,
                    numSecondaryBodies: 0, world.NumDynamicBodies, world.NumStaticBodies,  // Secondary bodies are not supported yet
                    bodies, UnsafeUtility.SizeOf<RigidBody>(),
                    motionDatas, sizeof(MotionData),
                    motionVelocities, sizeof(MotionVelocity),
                    joints, world.NumJoints, UnsafeUtility.SizeOf<Joint>(), syncStaticBodies);
            }

            if (!simulationContext.StaticBodiesSyncedOnce)
            {
                simulationContext.StaticBodiesSyncedOnce = true;
            }

            // Step the simulation
            if (world.NumDynamicBodies > 0)
            {
                unsafe
                {
                    // Start the step
                    {
                        var stepInput = new StepInput
                        {
                            m_timeStep = input.TimeStep,
                            m_gravity = input.Gravity,
                            m_numSolverIterations = input.NumSolverIterations,
                            m_numThreads = 1
                        };
                        Plugin.HP_StepWorld(simulationContext.WorldIndex, ref stepInput, simulationContext.StepContext);
                    }

                    // Apply gravity and copy input velocities.
                    // Note: Havok Physics doesn't "see" this gravity applied, but instead applies it on its own,
                    // but we need the velocity with applied gravity for later calculations as input velocity.
                    Solver.ApplyGravityAndCopyInputVelocities(world.MotionVelocities, simulationContext.InputVelocities, input.TimeStep * input.Gravity);

                    // Broadphase
                    Plugin.HP_ProcessStep(simulationContext.StepContext->broadphase);

                    // Narrowphase
                    Plugin.HP_ProcessStep(simulationContext.StepContext->narrowphase);

                    // Create Jacobians
                    Plugin.HP_ProcessStep(simulationContext.StepContext->solverPrep);

                    // Solve Jacobians and integrate
                    Plugin.HP_ProcessStep(simulationContext.StepContext->solverSolve);
                }
            }

            // Step the visual debugger server
            // Do this before extracting the motions so that velocities applied by VDB mouse picking are respected
            if (simulationContext.VisualDebuggerEnabled)
            {
                Plugin.HP_StepVisualDebugger(simulationContext.WorldIndex, input.TimeStep, ref simulationContext.Camera);
            }

            // Extract the updated motion states from the Havok world
            if (world.NumDynamicBodies > 0)
            {
                unsafe
                {
                    int numMotions = world.MotionDatas.Length;
                    Plugin.HP_SyncMotionsOut(
                        simulationContext.WorldIndex,
                        (MotionData*)world.MotionDatas.GetUnsafePtr(), numMotions, sizeof(MotionData),
                        (MotionVelocity*)world.MotionVelocities.GetUnsafePtr(), numMotions, sizeof(MotionVelocity),
                        0, numMotions);

                    // Synchronize transforms
                    if (input.SynchronizeCollisionWorld)
                    {
                        world.CollisionWorld.UpdateDynamicTree(ref world, input.TimeStep, input.Gravity);
                    }
                }
            }
        }

        public void Step(SimulationStepInput input)
        {
            ref PhysicsWorld world = ref input.World;
            m_SimulationContext.Reset(ref world);
            StepImmediate(input, ref m_SimulationContext);
        }

        [Obsolete("ScheduleStepJobs() has been deprecated. Please use the new method taking a bool as the last parameter. (RemovedAfter 2021-02-15)", true)]
        public SimulationJobHandles ScheduleStepJobs(SimulationStepInput input, SimulationCallbacks callbacksIn, JobHandle inputDeps, int threadCountHint = 0)
        {
            return ScheduleStepJobs(input, callbacksIn, inputDeps, threadCountHint > 0);
        }

        public SimulationJobHandles ScheduleStepJobs(SimulationStepInput input, SimulationCallbacks callbacksIn, JobHandle inputDeps, bool multiThreaded = true)
        {
            ref PhysicsWorld world = ref input.World;

            m_SimulationContext.Reset(ref world);
            m_SimulationContext.TimeStep = input.TimeStep;

            // Thread count is +1 for main thread
            int numThreads = multiThreaded ? (JobsUtility.JobWorkerCount + 1) : 1;

            // Early out if there will be no effective result
            if (world.NumDynamicBodies == 0 && !VisualDebuggerEnabled)
            {
                m_StepHandles = new SimulationJobHandles(inputDeps);
                return m_StepHandles;
            }

            JobHandle handle = new SyncJob
            {
                World = world,
                WorldIndex = WorldIndex,
                ForceStaticBodySync = m_SimulationContext.StaticBodiesSyncedOnce ? 0 : 1,
                HaveStaticBodiesChanged = m_SimulationContext.HaveStaticBodiesChanged
            }.Schedule(inputDeps);

            if (!m_SimulationContext.StaticBodiesSyncedOnce)
            {
                m_SimulationContext.StaticBodiesSyncedOnce = true;
            }

            SimulationCallbacks callbacks = callbacksIn ?? new SimulationCallbacks();

            // Step the Havok Physics world if necessary
            if (world.NumDynamicBodies > 0)
            {
                unsafe
                {
                    // Note: Havok Physics doesn't "see" this gravity applied, but instead applies it on its own,
                    // but we need the velocity with applied gravity for later calculations as input velocity.
                    var applyGravityAndCopyInputVelocitiesHandle = Solver.ScheduleApplyGravityAndCopyInputVelocitiesJob(
                        world.DynamicsWorld.MotionVelocities, m_SimulationContext.InputVelocities, input.TimeStep * input.Gravity, handle, multiThreaded);

                    // Generate context for this step
                    handle = new StartStepJob
                    {
                        WorldIndex = WorldIndex,
                        StepInput = new StepInput
                        {
                            m_timeStep = input.TimeStep,
                            m_gravity = input.Gravity,
                            m_numSolverIterations = input.NumSolverIterations,
                            m_numThreads = numThreads
                        },
                        StepContext = PhysicsStepContext
                    }.Schedule(handle);

                    // Make sure applying of gravity is done before any callbacks
                    handle = JobHandle.CombineDependencies(handle, applyGravityAndCopyInputVelocitiesHandle);

                    // Broad phase
                    if (callbacks.Any(SimulationCallbacks.Phase.PostCreateDispatchPairs))
                    {
                        handle = new StepJob(PhysicsStepContext, SimulationPhase.CreateDispatchPairs).Schedule(numThreads, 1, handle);
                        handle = callbacks.Execute(SimulationCallbacks.Phase.PostCreateDispatchPairs, this, ref world, handle);
                    }

                    // Narrow phase
                    if (callbacks.Any(SimulationCallbacks.Phase.PostCreateContacts))
                    {
                        handle = new StepJob(PhysicsStepContext, SimulationPhase.CreateContacts).Schedule(numThreads, 1, handle);
                        handle = callbacks.Execute(SimulationCallbacks.Phase.PostCreateContacts, this, ref world, handle);
                    }

                    // Create Jacobians
                    if (callbacks.Any(SimulationCallbacks.Phase.PostCreateContactJacobians))
                    {
                        handle = new StepJob(PhysicsStepContext, SimulationPhase.CreateContactJacobians).Schedule(numThreads, 1, handle);
                        handle = callbacks.Execute(SimulationCallbacks.Phase.PostCreateContactJacobians, this, ref world, handle);
                    }

                    // Solve Jacobians
                    handle = new StepJob(PhysicsStepContext, SimulationPhase.SolveJacobians).Schedule(numThreads, 1, handle);
                    handle = JobHandle.CombineDependencies(handle, applyGravityAndCopyInputVelocitiesHandle);
                }
            }

            // Step the visual debugger server
            // Do this before extracting the motions so that velocities applied by VDB mouse picking are respected
            if (VisualDebuggerEnabled)
            {
                handle = new StepVdbJob
                {
                    WorldIndex = WorldIndex,
                    TimeStep = input.TimeStep,
                    Camera = m_SimulationContext.Camera
                }.Schedule(handle);
            }

            // Extract the updated motion states from the Havok world
            if (world.NumDynamicBodies > 0)
            {
                if (callbacks.Any(SimulationCallbacks.Phase.PostSolveJacobians))
                {
                    handle = callbacks.Execute(SimulationCallbacks.Phase.PostSolveJacobians, this, ref world, handle);
                }

                const int syncMotionsBatchSize = 128;
                int numBatches = (world.MotionDatas.Length + syncMotionsBatchSize - 1) / syncMotionsBatchSize;
                handle = new ExtractMotionsJob
                {
                    MotionDatas = world.DynamicsWorld.MotionDatas,
                    MotionVelocities = world.DynamicsWorld.MotionVelocities,
                    WorldIndex = WorldIndex,
                    BatchSize = syncMotionsBatchSize
                }.Schedule(numBatches, 1, handle);

                if (input.SynchronizeCollisionWorld)
                {
                    // TODO: timeStep = 0 here for tighter bounds, since it will be rebuilt next step anyway?
                    handle = world.CollisionWorld.ScheduleUpdateDynamicTree(ref world, input.TimeStep, input.Gravity, handle, multiThreaded);
                }
            }

            m_StepHandles = new SimulationJobHandles(handle);
            return m_StepHandles;
        }

        #region Jobs

#if !UNITY_IOS || UNITY_EDITOR
        [BurstCompile]
#endif
        struct SyncJob : IJob
        {
            public int WorldIndex;
            [ReadOnly] public PhysicsWorld World;

            public int ForceStaticBodySync;

            // HaveStaticBodiesChanged can have default value, in which case we are not accessing it.
            // That's why we have [NativeDisableContainerSafetyRestriction]
            [NativeDisableContainerSafetyRestriction]
            [ReadOnly] public NativeArray<int> HaveStaticBodiesChanged;

            public unsafe void Execute()
            {
                var bodies = World.Bodies.GetUnsafeReadOnlyPtr();
                var motionDatas = (MotionData*)World.MotionDatas.GetUnsafeReadOnlyPtr();
                var motionVelocities = (MotionVelocity*)World.MotionVelocities.GetUnsafeReadOnlyPtr();
                var joints = World.Joints.GetUnsafeReadOnlyPtr();

                Plugin.HP_SyncWorldIn(
                    WorldIndex,
                    numSecondaryBodies: 0, World.NumDynamicBodies, World.NumStaticBodies,  // Secondary bodies are not supported yet
                    bodies, UnsafeUtility.SizeOf<RigidBody>(),
                    motionDatas, sizeof(MotionData),
                    motionVelocities, sizeof(MotionVelocity),
                    joints, World.NumJoints, UnsafeUtility.SizeOf<Joint>(),
                    ForceStaticBodySync == 1 || HaveStaticBodiesChanged == default ||  HaveStaticBodiesChanged[0] == 1);
            }
        }

#if !UNITY_IOS || UNITY_EDITOR
        [BurstCompile]
#endif
        unsafe struct StartStepJob : IJob
        {
            public int WorldIndex;
            public StepInput StepInput;
            [NativeDisableUnsafePtrRestriction] public StepContext* StepContext;

            public void Execute()
            {
                Plugin.HP_StepWorld(WorldIndex, ref StepInput, StepContext);
            }
        }

#if !UNITY_IOS || UNITY_EDITOR
        [BurstCompile]
#endif
        unsafe struct StepJob : IJobParallelFor
        {
            // TODO : WorldIndex
            public readonly SimulationPhase StopAtPhase;
            [NativeDisableUnsafePtrRestriction] public readonly StepContext* StepContext;

            public StepJob(StepContext* stepContext, SimulationPhase stopAtPhase)
            {
                StepContext = stepContext;
                StopAtPhase = stopAtPhase;
            }

            public void Execute(int i)
            {
                switch (StopAtPhase)
                {
                    case SimulationPhase.CreateDispatchPairs:
                        Plugin.HP_ProcessStep(StepContext->broadphase); break;
                    case SimulationPhase.CreateContacts:
                        Plugin.HP_ProcessStep(StepContext->narrowphase); break;
                    case SimulationPhase.CreateContactJacobians:
                        Plugin.HP_ProcessStep(StepContext->solverPrep); break;
                    case SimulationPhase.SolveJacobians:
                        Plugin.HP_ProcessStep(StepContext->solverSolve); break;
                    case SimulationPhase.SolveSecondaryBodies:
                    default:
                        Plugin.HP_ProcessStep(StepContext->particles); break;
                }
            }
        }

#if !UNITY_IOS || UNITY_EDITOR
        [BurstCompile]
#endif
        unsafe struct ExtractMotionsJob : IJobParallelFor
        {
            public int WorldIndex;
            public int BatchSize;
            public NativeArray<MotionData> MotionDatas;
            public NativeArray<MotionVelocity> MotionVelocities;

            public void Execute(int batchIndex)
            {
                int numMotions = MotionDatas.Length;
                int endIdx = math.min(numMotions, BatchSize * (batchIndex + 1));
                int numInBatch = endIdx - (BatchSize * batchIndex);

                Plugin.HP_SyncMotionsOut(
                    WorldIndex,
                    (MotionData*)MotionDatas.GetUnsafePtr(), numMotions, sizeof(MotionData),
                    (MotionVelocity*)MotionVelocities.GetUnsafePtr(), numMotions, sizeof(MotionVelocity),
                    BatchSize * batchIndex, numInBatch);
            }
        }

#if !UNITY_IOS || UNITY_EDITOR
        [BurstCompile]
#endif
        struct StepVdbJob : IJob
        {
            public int WorldIndex;
            public float TimeStep;
            public Camera Camera;

            public void Execute()
            {
                Plugin.HP_StepVisualDebugger(WorldIndex, TimeStep, ref Camera);
            }
        }

        #endregion
    }
}
