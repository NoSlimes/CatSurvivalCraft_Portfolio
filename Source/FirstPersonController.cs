using Cinemachine;
using NoSlimes.Gameplay.Core;
using NoSlimes.Logging;
using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace NoSlimes.Gameplay
{
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : NetworkBehaviour
    {
        [Header("Player")]
        [SerializeField] private float sprintMultiplier = 1.5f;
        [SerializeField] private float rotationSpeed = 1.0f;
        [SerializeField] private float speedChangeRate = 10.0f;

        [Space(10)]
        [SerializeField] private float jumpHeight = 1.2f;
        [SerializeField] private float gravity = -15.0f;

        [Space(10)]
        [SerializeField] private float jumpTimeout = 0.1f;
        [SerializeField] private float fallTimeout = 0.15f;

        [Header("Player Grounded")]
        [SerializeField] private bool grounded = true;
        [SerializeField] private float groundedOffset = -0.14f;
        [SerializeField] private float groundedRadius = 0.5f;
        [SerializeField] private LayerMask groundLayers;

        [Header("Cinemachine")]
        [SerializeField] private GameObject cinemachineCameraTarget;
        [SerializeField] private float topClamp = 90.0f;
        [SerializeField] private float bottomClamp = -90.0f;

        private readonly NetworkVariable<float> movementSpeed = new();
        private readonly NetworkVariable<float> sprintSpeed = new();
        private float currentSpeed;
        private float rotationVelocity;
        private float verticalVelocity;
        private float terminalVelocity = 53.0f;
        private float jumpTimeoutDelta;
        private float fallTimeoutDelta;

        private float _cinemachineTargetPitch;

        private Animator animator;
        private Character character;
        private CharacterController controller;
        private InputManager inputManager;
        private GameObject mainCameraGameObject;
        private EntityStats entityStats;

        private readonly Queue<PlayerMovementInputData> inputQueue = new();
        private readonly NetworkVariable<float> netCameraPitch = new();

        private const float rotationThreshold = 0.01f;
        private bool IsCurrentDeviceMouse => inputManager.PlayerInput.currentControlScheme == "KeyboardMouse";

        private void Awake()
        {
            if (mainCameraGameObject == null && Camera.main != null)
            {
                mainCameraGameObject = Camera.main.gameObject;
            }

            animator = GetComponentInChildren<Animator>();
            character = GetComponent<Character>();
            controller = GetComponent<CharacterController>();
            entityStats = GetComponent<EntityStats>();

            inputManager = InputManager.Instance;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (IsOwner)
            {
                if (cinemachineCameraTarget != null && mainCameraGameObject != null)
                {
                    CinemachineVirtualCamera cinemachineCam = FindAnyObjectByType<CinemachineVirtualCamera>();
                    if (cinemachineCam != null)
                    {
                        cinemachineCam.Follow = cinemachineCameraTarget.transform;
                    }
                    else
                    {
                        DLog.DevLogWarning($"[{gameObject.name}] Cinemachine Virtual Camera not found in scene.", this);
                    }
                }
            }

            if (!IsServer && !IsOwner)
            {
                controller.enabled = false;
            }

            if (IsServer)
            {
                if (entityStats.IsInitialized)
                {
                    SetupStats();
                }
                else
                {
                    entityStats.OnStatsInitialized += SetupStats;
                }
            }
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();

            if (IsServer && entityStats != null)
            {
                entityStats.OnStatsInitialized -= SetupStats;
            }
        }

        private void SetupStats()
        {
            movementSpeed.Value = entityStats.GetEffectiveStat(EntityStatType.MovementSpeed);
            sprintSpeed.Value = movementSpeed.Value * sprintMultiplier;

            if (movementSpeed.Value <= 0.0f)
            {
                DLog.DevLogError($"[{gameObject.name}] Movement speed is set to zero or negative. Please check the EntityStats configuration.", this);
            }

            if (sprintSpeed.Value <= 0.0f)
            {
                DLog.DevLogError($"[{gameObject.name}] Sprint speed is set to zero or negative. Please check the EntityStats configuration.", this);
            }
        }

        private void Update()
        {
            if (IsServer)
            {
                while (inputQueue.Count > 0)
                {
                    var inputData = inputQueue.Dequeue();
                    ProcessPlayerMovement(inputData, inputData.deltaTime);
                    UpdateAnimatorParameters(inputData);
                }
            }

            if (IsOwner)
            {
                var currentFrameInput = new PlayerMovementInputData
                {
                    move = inputManager.Move,
                    look = inputManager.Look,
                    Sprint = inputManager.IsSprinting,
                    Jump = inputManager.JumpPressed,
                    deltaTime = Time.deltaTime
                };

                if (!IsHost)
                    ProcessPlayerMovement(currentFrameInput, Time.deltaTime); // Locally predict movement, not on host in order to prevent double processing.

                SendInputToServerRpc(currentFrameInput);
            }
        }

        private void LateUpdate()
        {
            if (!IsOwner)
            {
                if (netCameraPitch.Value != _cinemachineTargetPitch)
                {
                    cinemachineCameraTarget.transform.localRotation = Quaternion.Euler(netCameraPitch.Value, 0.0f, 0.0f);
                }
            }
        }

        [Rpc(SendTo.Server, RequireOwnership = true)]
        private void SendInputToServerRpc(PlayerMovementInputData inputData)
        {
            inputQueue.Enqueue(inputData);
        }

        private void ProcessPlayerMovement(PlayerMovementInputData inputData, float frameDeltaTime)
        {
            GroundedCheck();
            JumpAndGravity(inputData, frameDeltaTime);
            Move(inputData, frameDeltaTime);
            CameraRotation(inputData, frameDeltaTime);
        }

        private void UpdateAnimatorParameters(PlayerMovementInputData inputData)
        {
            float targetSpeed = inputData.Sprint ? sprintSpeed.Value : movementSpeed.Value;
            float speedRatio = currentSpeed / Mathf.Max(targetSpeed, 0.001f);

            animator.SetFloat("Speed", speedRatio);
            animator.SetBool("Walking", currentSpeed > 0.1f);

        }

        private void GroundedCheck()
        {
            Vector3 spherePosition = new(transform.position.x, transform.position.y - groundedOffset, transform.position.z);
            grounded = Physics.CheckSphere(spherePosition, groundedRadius, groundLayers, QueryTriggerInteraction.Ignore);
        }

        private void CameraRotation(PlayerMovementInputData currentInput, float frameDeltaTime)
        {
            if (currentInput.look.sqrMagnitude >= rotationThreshold)
            {
                float lookSensitivityFactor = IsCurrentDeviceMouse ? 1.0f : frameDeltaTime;

                _cinemachineTargetPitch += currentInput.look.y * rotationSpeed * lookSensitivityFactor;
                rotationVelocity = currentInput.look.x * rotationSpeed * lookSensitivityFactor;

                _cinemachineTargetPitch = ClampAngle(_cinemachineTargetPitch, bottomClamp, topClamp);

                if (IsServer)
                {
                    netCameraPitch.Value = _cinemachineTargetPitch;
                }

                cinemachineCameraTarget.transform.localRotation = Quaternion.Euler(_cinemachineTargetPitch, 0.0f, 0.0f);
                transform.Rotate(Vector3.up * rotationVelocity);
            }
        }

        private void Move(PlayerMovementInputData currentInput, float frameDeltaTime)
        {
            float targetSpeed = currentInput.Sprint ? sprintSpeed.Value : movementSpeed.Value;
            if (currentInput.move == Vector2.zero)
                targetSpeed = 0.0f;

            float inputMagnitude = !IsCurrentDeviceMouse ? currentInput.move.magnitude : 1f;

            currentSpeed = Mathf.Lerp(currentSpeed, targetSpeed * inputMagnitude, frameDeltaTime * speedChangeRate);

            Vector3 inputDirection = Vector3.zero;
            if (currentInput.move != Vector2.zero)
            {
                inputDirection = (transform.right * currentInput.move.x + transform.forward * currentInput.move.y).normalized;
            }

            if (controller.enabled)
            {
                controller.Move(inputDirection * (currentSpeed * frameDeltaTime) +
                                new Vector3(0.0f, verticalVelocity, 0.0f) * frameDeltaTime);
            }
        }

        private void JumpAndGravity(PlayerMovementInputData currentInput, float frameDeltaTime)
        {
            if (grounded)
            {
                fallTimeoutDelta = fallTimeout;
                if (verticalVelocity < 0.0f) verticalVelocity = -2f;

                if (currentInput.Jump && jumpTimeoutDelta <= 0.0f)
                {
                    verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
                }

                if (jumpTimeoutDelta >= 0.0f) jumpTimeoutDelta -= frameDeltaTime;
            }
            else
            {
                jumpTimeoutDelta = jumpTimeout;
                if (fallTimeoutDelta >= 0.0f)
                {
                    fallTimeoutDelta -= frameDeltaTime;
                }
            }

            if (verticalVelocity < terminalVelocity)
            {
                verticalVelocity += gravity * frameDeltaTime;
            }
        }

        private static float ClampAngle(float lfAngle, float lfMin, float lfMax)
        {
            if (lfAngle < -360f) lfAngle += 360f;
            if (lfAngle > 360f) lfAngle -= 360f;
            return Mathf.Clamp(lfAngle, lfMin, lfMax);
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Color transparentGreen = new(0.0f, 1.0f, 0.0f, 0.35f);
            Color transparentRed = new(1.0f, 0.0f, 0.0f, 0.35f);

            if (grounded) Gizmos.color = transparentGreen;
            else Gizmos.color = transparentRed;

            Gizmos.DrawSphere(new Vector3(transform.position.x, transform.position.y - groundedOffset, transform.position.z), groundedRadius);
        }
#endif
    }

    public struct PlayerMovementInputData : INetworkSerializable, IEquatable<PlayerMovementInputData>
    {
        public Vector2 move;
        public Vector2 look;
        public byte packedBools;
        public float deltaTime;

        public bool Sprint
        {
            readonly get => (packedBools & (1 << 0)) != 0;
            set
            {
                if (value) packedBools |= 1 << 0;
                else packedBools &= unchecked((byte)~(1 << 0));
            }
        }

        public bool Jump
        {
            readonly get => (packedBools & (1 << 1)) != 0;
            set
            {
                if (value) packedBools |= 1 << 1;
                else packedBools &= unchecked((byte)~(1 << 1));
            }
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref move);
            serializer.SerializeValue(ref look);
            serializer.SerializeValue(ref packedBools);
            serializer.SerializeValue(ref deltaTime);
        }

        public readonly bool Equals(PlayerMovementInputData other)
        {
            return move == other.move &&
                   look == other.look &&
                   packedBools == other.packedBools &&
                   deltaTime == other.deltaTime;
        }

        public override readonly bool Equals(object obj) => obj is PlayerMovementInputData other && Equals(other);

        public override readonly int GetHashCode() => HashCode.Combine(move, look, packedBools, deltaTime);

        public static bool operator ==(PlayerMovementInputData left, PlayerMovementInputData right) => left.Equals(right);
        public static bool operator !=(PlayerMovementInputData left, PlayerMovementInputData right) => !left.Equals(right);

    }
}
