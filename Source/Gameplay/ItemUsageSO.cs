using NoSlimes.Logging;
using Unity.Netcode;
using UnityEngine;

namespace NoSlimes.Gameplay
{
    public struct ItemUsageContext : INetworkSerializable
    {
        public Vector3 AimDirection;
        public Vector3 OriginPosition;

        public ItemUsageContext(Vector3 aimDirection, Vector3 cameraPosition)
        {
            AimDirection = aimDirection;
            OriginPosition = cameraPosition;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref AimDirection);
            serializer.SerializeValue(ref OriginPosition);
        }
    }

    public abstract class ItemUsageSO : ScriptableObject
    {
        public void Use(NetworkObject user, ItemData data, ItemInstanceData instanceData, ItemUsageContext context)
        {
            if (!NetworkManager.Singleton.IsServer)
            {
                DLog.DevLogError("Item usage can only be performed on the server.", this);
                return;
            }

            if (user == null || data == null)
            {
                DLog.DevLogError("Invalid user or item data provided for item usage.", this);
                return;
            }

            try
            {
                UseInternal(user, data, instanceData, context);
            }
            catch (System.Exception ex)
            {
                DLog.DevLogError($"Error using item: {ex.Message}", this);
#if UNITY_EDITOR
                throw;
#endif
            }
        }

        protected abstract void UseInternal(NetworkObject user, ItemData data, ItemInstanceData instanceData, ItemUsageContext context);
        public virtual void TriggerOwnerEffects(GameObject user) { }
        public virtual void TriggerReplicatedEffects(GameObject user) { }
    }
}
