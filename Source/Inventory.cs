using NoSlimes.Logging;
using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace NoSlimes.Gameplay
{
    public enum InventoryAccess
    {
        OwnerOnly,
        Everyone,
        OwnerOnlyProximity,
        EveryoneProximity
    }

    public class Inventory : NetworkBehaviour
    {
        public static readonly DLogCategory LogInventory = new("Inventory", Color.cyan);

        [Header("Inventory Settings")]
        [SerializeField] private InventoryAccess inventoryAccess = InventoryAccess.OwnerOnly;

        [SerializeField, Tooltip("Only applicable if using a proximity based access value")]
        private float proximityRange = 5f;

        [Header("Slots")]
        [SerializeField] private int defaultSlots = 20;

        private readonly NetworkVariable<int> additionalSlots = new(0);
        public virtual int SlotCount => defaultSlots + additionalSlots.Value;

        protected readonly List<ItemStack> inventoryItems = new();
        protected readonly Dictionary<Guid, ItemInstanceData> instanceDataMap = new();

        // Client-side copy of inventory items for UI updates
        private List<ItemStack> inventoryItemsClient = new();
        public event Action OnInventoryItemsChanged;

        #region Unity Lifecycle
        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (IsServer && inventoryItems.Count == 0)
            {
                for (int i = 0; i < SlotCount; i++)
                    inventoryItems.Add(new ItemStack { ItemID = new ItemID(0), Quantity = 0 });

                NotifyClientsOfChange();
                DLog.DevLog($"Initialized inventory with {SlotCount} slots.", this, LogInventory);
            }
        }
        #endregion

        #region Inventory Sync
        [ClientRpc]
        private void ReceiveInventoryClientRpc(ItemStack[] inventorySnapshot, ClientRpcParams clientRpcParams = default)
        {
            inventoryItemsClient = new List<ItemStack>(inventorySnapshot);
            OnInventoryItemsChanged?.Invoke(); 
        }

        public void SendInventoryToClient(ulong clientId)
        {
            if (!CanClientAccess(clientId))
            {
                DLog.DevLogWarning($"Client {clientId} tried to access inventory without permission.", this, LogInventory);
                return;
            }

            ItemStack[] snapshot = inventoryItems.ToArray();

            ClientRpcParams rpcParams = new()
            {
                Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } }
            };

            ReceiveInventoryClientRpc(snapshot, rpcParams);
        }

        [Rpc(SendTo.Server, RequireOwnership = false)]
        public void RequestInventoryServerRpc(RpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;

            SendInventoryToClient(clientId);
        }

        protected void NotifyClientsOfChange()
        {
            foreach (ulong clientId in NetworkManager.ConnectedClientsIds)
            {
                if (CanClientAccess(clientId))
                {
                    SendInventoryToClient(clientId);
                }
            }
        }
        #endregion

        [Rpc(SendTo.Server)]
        public void SwapSlotsServerRpc(int fromIndex, int toIndex)
        {
            SwapSlotsInternal(fromIndex, toIndex);
        }
        private void SwapSlotsInternal(int fromIndex, int toIndex)
        {
            if (!IsServer)
            {
                DLog.DevLogWarning("SwapSlotsInternal can only be called on the server.", this, LogInventory);
                return;
            }

            InventoryUtil.TransferItemToSlot(this, fromIndex, this, toIndex);
            NotifyClientsOfChange();
        }

        [Rpc(SendTo.Server)]
        public void SwapSlotToInventoryServerRpc(int fromIndex, ulong targetInventoryNetworkID, int toIndex = -1, RpcParams rpcParams = default)
        {
            ulong senderClientId = rpcParams.Receive.SenderClientId;

            if (!this.CanClientAccess(senderClientId))
            {
                DLog.DevLogWarning($"Client {senderClientId} tried to send from inventory they can't access.", this, LogInventory);
                return;
            }

            NetworkObject networkObject = NetworkManager.SpawnManager.SpawnedObjects[targetInventoryNetworkID];
            if (networkObject == null)
            {
                DLog.DevLogError($"Target inventory with Network ID {targetInventoryNetworkID} not found.", this, LogInventory);
                return;
            }

            Inventory targetInventory = networkObject.GetComponent<Inventory>();
            if (targetInventory == null)
            {
                DLog.DevLogError($"Target inventory component not found on NetworkObject with ID {targetInventoryNetworkID}.", this, LogInventory);
                return;
            }

            if (!targetInventory.CanClientAccess(senderClientId))
            {
                DLog.DevLogWarning($"Client {senderClientId} tried to send to inventory they can't access.", this, LogInventory);
                return;
            }

            SwapSlotToInventoryInternal(fromIndex, targetInventory, toIndex);
        }

        [Rpc(SendTo.Server)]
        public void MoveAmountToInventoryServerRpc(int fromIndex, ushort quantity, ulong targetInventoryNetworkID, int toIndex = -1, RpcParams rpcParams = default)
        {
            ulong senderClientId = rpcParams.Receive.SenderClientId;

            if (!this.CanClientAccess(senderClientId))
            {
                DLog.DevLogWarning($"Client {senderClientId} tried to send from inventory they can't access.", this, LogInventory);
                return;
            }

            if (!IsServer)
            {
                DLog.DevLogWarning("MoveAmountToInventoryServerRpc called on client instead of server.", this, LogInventory);
                return;
            }

            if (fromIndex < 0 || fromIndex >= inventoryItems.Count)
            {
                DLog.DevLogWarning($"Invalid fromIndex {fromIndex}.", this, LogInventory);
                return;
            }

            ItemStack sourceSlot = inventoryItems[fromIndex];
            if (sourceSlot.Quantity < quantity || quantity == 0)
            {
                DLog.DevLogWarning($"Not enough quantity in slot {fromIndex} to move {quantity}.", this, LogInventory);
                return;
            }

            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(targetInventoryNetworkID, out NetworkObject networkObject))
            {
                DLog.DevLogError($"Target inventory with Network ID {targetInventoryNetworkID} not found.", this, LogInventory);
                return;
            }

            Inventory targetInventory = networkObject.GetComponentInChildren<Inventory>();
            if (targetInventory == null)
            {
                DLog.DevLogError($"Target inventory component not found on NetworkObject with ID {targetInventoryNetworkID}.", this, LogInventory);
                return;
            }

            if (!targetInventory.CanClientAccess(senderClientId))
            {
                DLog.DevLogWarning($"Client {senderClientId} tried to send to inventory they can't access.", this, LogInventory);
                return;
            }

            targetInventory.TryAddAsManyItemsAsPossible(sourceSlot.ItemID, quantity, out ushort remainder, toIndex);
            if (remainder > 0)
            {
                DLog.DevLogWarning($"Could not move all items. Requested: {quantity}, Moved: {quantity - remainder}, Remaining: {remainder}", this, LogInventory);
            }

            RemoveItemAtSlot(fromIndex, sourceSlot.ItemID, (ushort)(quantity - remainder));
        }

        public bool CanClientAccess(ulong clientID)
        {
            switch (inventoryAccess)
            {
                case InventoryAccess.OwnerOnly:
                    return OwnerClientId == clientID;
                case InventoryAccess.Everyone:
                    return true;
                case InventoryAccess.OwnerOnlyProximity:
                    return OwnerClientId == clientID && IsClientNearby(clientID);
                case InventoryAccess.EveryoneProximity:
                    return IsClientNearby(clientID);
                default:
                    DLog.DevLogWarning($"Unknown inventory access type: {inventoryAccess}", this, LogInventory);
                    return false;
            }
        }

        public float GetProximityRange()
        {
            return proximityRange;
        }

        private bool IsClientNearby(ulong clientID)
        {
            NetworkObject playerObject = NetworkManager.SpawnManager.GetPlayerNetworkObject(clientID);

            if (playerObject == null)
            {
                DLog.DevLogWarning($"Player with Client ID {clientID} not found.", this, LogInventory);
                return false;
            }

            float distance = Vector3.Distance(transform.position, playerObject.transform.position);
            return distance <= proximityRange;
        }

        private void SwapSlotToInventoryInternal(int fromIndex, Inventory target, int toIndex = -1)
        {
            if (!IsServer)
            {
                DLog.DevLogWarning("SwapSlotToInventoryInternal can only be called on the server.", this, LogInventory);
                return;
            }

            if (target == null)
            {
                DLog.DevLogError("Target inventory is null.", this, LogInventory);
                return;
            }

            InventoryUtil.TransferItemToSlot(this, fromIndex, target, toIndex);
            NotifyClientsOfChange();
        }

        #region Item Addition
        public bool AddItem(ItemID item, ushort quantity, int? slotIndex = null)
        {
            if (!IsServer)
            {
                DLog.DevLogWarning("AddItem can only be called on the server.", this, LogInventory);
                return false;
            }

            if (quantity == 0)
                return true;

            ItemData itemData = ItemDatabaseSO.Instance.GetItemByID(item);
            if (itemData == null)
            {
                DLog.DevLogError($"Item {item.ID} not found in database.", this, LogInventory);
                return false;
            }

            ushort stackSize = itemData.StackSize;

            if (slotIndex.HasValue)
            {
                return AddItemToSlotInternal(slotIndex.Value, item, quantity, stackSize);
            }

            return AddItemInternal(item, quantity, stackSize);
        }

        protected virtual bool AddItemInternal(ItemID item, ushort quantity, int stackSize)
        {
            ushort remaining = quantity;
            ItemData itemData = ItemDatabaseSO.Instance.GetItemByID(item);
            if (itemData == null)
            {
                DLog.DevLogError($"Item {item.ID} not found in database.", this, LogInventory);
                return false;
            }

            if (stackSize > 1)
            {
                for (int i = 0; i < inventoryItems.Count && remaining > 0; i++)
                {
                    var slot = inventoryItems[i];

                    if (slot.ItemID == item && !slot.HasInstanceData && slot.Quantity < stackSize)
                    {
                        ushort space = (ushort)(stackSize - slot.Quantity);
                        ushort toAdd = (ushort)Mathf.Min(space, remaining);

                        if (toAdd > 0)
                        {
                            slot.Quantity += toAdd;
                            inventoryItems[i] = slot;
                            remaining -= toAdd;
                            DLog.DevLog($"Stacked {toAdd} of item {item.ID} in slot {i}.", this, LogInventory);
                        }
                    }
                }
            }

            for (int i = 0; i < inventoryItems.Count && remaining > 0; i++)
            {
                var slot = inventoryItems[i];
                if (slot.ItemID == ItemID.INVALID_ID)
                {
                    Guid newInstanceId = CreateItemInstance(itemData);

                    if (newInstanceId != Guid.Empty)
                    {
                        // --- This is a UNIQUE item ---

                        slot.ItemID = item;
                        slot.Quantity = 1;
                        slot.InstanceID = newInstanceId;
                        inventoryItems[i] = slot;
                        remaining--;
                        DLog.DevLog($"Added INSTANCED item {item.ID} to new slot {i}.", this, LogInventory);
                    }
                    else
                    {
                        // --- This is a STACKABLE item ---

                        ushort toAdd = (ushort)Mathf.Min(stackSize, remaining);
                        slot.ItemID = item;
                        slot.Quantity = toAdd;
                        slot.InstanceID = Guid.Empty;
                        inventoryItems[i] = slot;
                        remaining -= toAdd;
                        DLog.DevLog($"Added {toAdd} of STACKABLE item {item.ID} to new slot {i}.", this, LogInventory);
                    }
                }
            }

            NotifyClientsOfChange();
            return remaining == 0;
        }

        protected virtual bool AddItemToSlotInternal(int slotIndex, ItemID item, ushort quantity, int stackSize)
        {
            if (slotIndex < 0 || slotIndex >= inventoryItems.Count)
            {
                DLog.DevLogWarning($"Invalid slot index {slotIndex}.", this, LogInventory);
                return false;
            }

            ItemStack slot = inventoryItems[slotIndex];

            if (slot.ItemID == ItemID.INVALID_ID)
            {
                Guid newInstanceId = CreateItemInstance(ItemDatabaseSO.Instance.GetItemByID(item));

                ushort toAdd = (ushort)Mathf.Min(quantity, stackSize);
                if (toAdd == 0) return false;

                if (newInstanceId != Guid.Empty && toAdd > 1)
                {
                    DLog.DevLogWarning($"Cannot add a stack of instanced items to a single slot. Adding one.", this, LogInventory);
                    toAdd = 1;
                }

                slot.ItemID = item;
                slot.Quantity = toAdd;
                slot.InstanceID = newInstanceId;
                inventoryItems[slotIndex] = slot;

                DLog.DevLog($"Added {toAdd} of item {item.ID} to empty slot {slotIndex}.", this, LogInventory);
                NotifyClientsOfChange();
                return toAdd == quantity;
            }

            if (slot.ItemID == item && !slot.HasInstanceData)
            {
                ushort space = (ushort)(stackSize - slot.Quantity);
                ushort toAdd = (ushort)Mathf.Min(space, quantity);

                if (toAdd > 0)
                {
                    slot.Quantity += toAdd;
                    inventoryItems[slotIndex] = slot;
                    DLog.DevLog($"Stacked {toAdd} of item {item.ID} onto slot {slotIndex}.", this, LogInventory);
                    NotifyClientsOfChange();
                }

                return toAdd == quantity;
            }

            DLog.DevLogWarning($"Slot {slotIndex} contains another item or is an instanced item. Cannot add {item.ID}.", this, LogInventory);
            return false;
        }

        public bool AddItemAtSlot(int slotIndex, ItemID item, ushort quantity) => AddItem(item, quantity, slotIndex);
        public bool TryAddAsManyItemsAsPossible(ItemID item, ushort quantity, out ushort remainder, int slotIndex = -1)
        {
            remainder = quantity;

            if (!IsServer)
            {
                DLog.DevLogWarning("TryAddAsManyItemsAsPossible must be called on server.", this, LogInventory);
                return false;
            }

            if (slotIndex >= 0 && (slotIndex < 0 || slotIndex >= SlotCount))
            {
                DLog.DevLogWarning($"Invalid slot index {slotIndex}. Must be between 0 and {SlotCount - 1}.", this, LogInventory);
                return false;
            }

            if (slotIndex == -1)
            {
                if (AddItem(item, quantity))
                {
                    remainder = 0;
                    return true;
                }

                for (int i = 0; i < quantity; i++)
                {
                    if (AddItem(item, 1))
                    {
                        remainder--;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else
            {
                if (AddItemAtSlot(slotIndex, item, quantity))
                {
                    remainder = 0;
                    return true;
                }

                for (int i = 0; i < quantity; i++)
                {
                    if (AddItemAtSlot(slotIndex, item, 1))
                    {
                        remainder--;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            DLog.DevLog($"Attempted to add {quantity} of {item.ID}, remainder: {remainder}", this, LogInventory);

            NotifyClientsOfChange();
            return remainder == 0;
        }

        #endregion

        #region Item Removal
        public bool RemoveItem(ItemID item, ushort quantity, int? slotIndex = null)
        {
            if (!IsServer)
            {
                DLog.DevLogWarning("RemoveItem can only be called on the server.", this, LogInventory);
                return false;
            }

            if (quantity == 0)
                return true;

            if (slotIndex.HasValue)
            {
                return RemoveItemFromSlotInternal(slotIndex.Value, item, quantity);
            }

            return RemoveItemInternal(item, quantity);
        }

        protected virtual bool RemoveItemInternal(ItemID item, ushort quantity)
        {
            // Count total quantity available
            ushort totalAvailable = 0;
            for (int i = 0; i < inventoryItems.Count; i++)
            {
                if (inventoryItems[i].ItemID == item)
                {
                    totalAvailable += inventoryItems[i].Quantity;
                    if (totalAvailable >= quantity)
                        break;
                }
            }

            if (totalAvailable < quantity)
            {
                DLog.DevLogWarning($"Not enough of item {item.ID} to remove. Requested: {quantity}, Available: {totalAvailable}", this, LogInventory);
                return false;
            }

            // Remove from stacks
            ushort remainingToRemove = quantity;
            for (int i = 0; i < inventoryItems.Count && remainingToRemove > 0; i++)
            {
                ItemStack slot = inventoryItems[i];
                if (slot.ItemID == item)
                {
                    if (slot.Quantity <= remainingToRemove)
                    {
                        remainingToRemove -= slot.Quantity;

                        if (slot.HasInstanceData)
                            instanceDataMap.Remove(slot.InstanceID);

                        slot.ItemID = ItemID.INVALID_ID;
                        slot.Quantity = 0;
                    }
                    else
                    {
                        slot.Quantity -= remainingToRemove;
                        remainingToRemove = 0;
                    }

                    inventoryItems[i] = slot;

                    DLog.DevLog($"Removed from slot {i}. Remaining to remove: {remainingToRemove}", this, LogInventory);
                }
            }

            NotifyClientsOfChange();
            return true;
        }
        protected virtual bool RemoveItemFromSlotInternal(int slotIndex, ItemID item, ushort quantity)
        {
            int index = slotIndex;
            if (index < 0 || index >= inventoryItems.Count)
            {
                DLog.DevLogWarning($"Invalid slot index {index}.", this, LogInventory);
                return false;
            }

            ItemStack slot = inventoryItems[index];

            if (slot.ItemID != item)
            {
                DLog.DevLogWarning($"Slot {index} does not contain expected item {item.ID}. Found: {slot.ItemID.ID}", this, LogInventory);
                return false;
            }

            if (slot.Quantity < quantity)
            {
                DLog.DevLogWarning($"Not enough items in slot {index} to remove. Requested: {quantity}, Available: {slot.Quantity}", this, LogInventory);
                return false;
            }

            slot.Quantity -= quantity;

            if (slot.Quantity == 0)
            {
                if (slot.HasInstanceData)
                    instanceDataMap.Remove(slot.InstanceID);

                slot.ItemID = ItemID.INVALID_ID;
            }

            inventoryItems[index] = slot;

            DLog.DevLog($"Removed {quantity} of item {item.ID} from slot {index}.", this, LogInventory);
            NotifyClientsOfChange();
            return true;
        }

        public bool RemoveItemAtSlot(int slotIndex, ItemID expectedItem, ushort quantity) => RemoveItem(expectedItem, quantity, slotIndex);
        #endregion

        protected Guid CreateItemInstance(ItemData itemData)
        {
            switch (itemData)
            {
                case ToolData toolData:
                    return CreateInstanceData(toolData, (itemID, instanceID) => new ToolInstanceData(itemID, instanceID, toolData.Durability)).InstanceID;

                case ConsumableItemData consumableData:
                    // Handle consumable creation
                    return Guid.Empty;

                    //case MaterialData materialData:
                    //    // Handle material creation
                    //    break;

                    //case MiscellaneousItemData miscData:
                    //    // Handle misc creation
                    //    break;

                    //case EquipmentData equipmentData:
                    //    // Handle equipment creation
                    //    break;
            }

            return Guid.Empty;
        }

        private TInstance CreateInstanceData<TData, TInstance>(TData itemData, Func<ItemID, Guid, TInstance> constructorFunc) where TData : ItemData where TInstance : ItemInstanceData
        {
            if (itemData is null)
            {
                DLog.DevLogError($"Item data type mismatch. Expected {typeof(TData)}, got {itemData.GetType()}.", this, LogInventory);
                return null;
            }

            Guid instanceID = Guid.NewGuid();
            TInstance instanceData = constructorFunc(itemData.ID, instanceID);
            instanceDataMap[instanceID] = instanceData;

            return instanceData;
        }

        public ItemInstanceData GetInstanceData(Guid instanceID)
        {
            if (instanceDataMap.TryGetValue(instanceID, out ItemInstanceData instanceData))
            {
                return instanceData;
            }

            DLog.DevLogWarning($"Instance data with ID {instanceID} not found.", this, LogInventory);
            return null;
        }

        public ItemStack GetItem(int index, ulong clientID = ulong.MinValue, bool ignoreServerRestriction = false)
        {
            if (clientID == ulong.MinValue)
                clientID = NetworkManager.LocalClientId;

            if (IsServer && !ignoreServerRestriction && !CanClientAccess(clientID))
            {
                DLog.DevLogWarning($"Server attempted to get inventory item for client {clientID} without access.", this, LogInventory);
                return new ItemStack { ItemID = ItemID.INVALID_ID, Quantity = 0 };
            }

            if (!IsServer && !CanClientAccess(clientID))
            {
                DLog.DevLogWarning($"Client {clientID} attempted to access inventory item without proper authority.", this, LogInventory);
                return new ItemStack { ItemID = ItemID.INVALID_ID, Quantity = 0 };
            }

            if (index < 0 || index >= SlotCount)
            {
                DLog.DevLogWarning($"GetItem index out of range: {index}", this, LogInventory);
                return new ItemStack { ItemID = ItemID.INVALID_ID, Quantity = 0 };
            }

            return IsServer ? inventoryItems[index] : inventoryItemsClient[index];
        }

        public ItemStack[] GetItems(ulong clientID = ulong.MinValue, bool ignoreServerRestriction = false)
        {
            if (clientID == ulong.MinValue)
                clientID = NetworkManager.LocalClientId;

            if (IsServer)
            {
                // If host acts like a client, and ignoreServerRestriction is false, apply access check. This is to prevent host from accessing client inventories without permission (eg. out of range, etc)
                if (!ignoreServerRestriction && NetworkManager.LocalClientId == clientID && !CanClientAccess(clientID))
                {
                    DLog.DevLogWarning($"Host (acting as client) tried to access inventory for client {clientID} without access.", this, LogInventory);
                    return Array.Empty<ItemStack>();
                }

                return inventoryItems.ToArray();
            }

            if (!CanClientAccess(clientID))
            {
                DLog.DevLogWarning($"Client {clientID} tried to access inventory without permission.", this, LogInventory);
                return Array.Empty<ItemStack>();
            }

            return inventoryItemsClient.ToArray();
        }

        public void SetSlot(int index, ItemStack slot)
        {
            if (!IsServer)
            {
                DLog.DevLogWarning("SetSlot can only be called on the server.", this, LogInventory);
                return;
            }

            if (index < 0 || index >= inventoryItems.Count)
            {
                DLog.DevLogWarning($"SetSlot index out of range: {index}", this, LogInventory);
                return;
            }

            inventoryItems[index] = slot;
        }

    }

    [Serializable]
    public struct ItemID : INetworkSerializable, IEquatable<ItemID>
    {
        public static readonly ItemID INVALID_ID = new(0);

        [SerializeField] private ushort id;
        public readonly ushort ID => id;

        public ItemID(ushort id) => this.id = id;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            => serializer.SerializeValue(ref id);

        public override readonly string ToString() => id.ToString();
        public readonly bool Equals(ItemID other) => id == other.id;
        public override readonly int GetHashCode() => id.GetHashCode();
        public override readonly bool Equals(object obj) => obj is ItemID other && Equals(other);

        public static bool operator ==(ItemID a, ItemID b) => a.Equals(b);
        public static bool operator !=(ItemID a, ItemID b) => !a.Equals(b);
    }

    [Serializable]
    public struct ItemStack : INetworkSerializable, IEquatable<ItemStack>
    {
        public ItemID ItemID;
        public ushort Quantity;

        public Guid InstanceID;
        public readonly bool HasInstanceData => InstanceID != Guid.Empty;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ItemID);
            serializer.SerializeValue(ref Quantity);

            if (serializer.IsWriter)
            {
                // On the server, convert Guid to bytes and write them
                byte[] guidBytes = InstanceID.ToByteArray();
                serializer.GetFastBufferWriter().WriteBytesSafe(guidBytes, 16);
            }
            else
            {
                // On the client, read the bytes and convert back to Guid
                byte[] guidBytes = new byte[16];
                serializer.GetFastBufferReader().ReadBytesSafe(ref guidBytes, 16);
                InstanceID = new Guid(guidBytes);
            }
        }

        public readonly bool Equals(ItemStack other) => ItemID == other.ItemID && Quantity == other.Quantity && InstanceID == other.InstanceID;
        public override readonly bool Equals(object obj) => Equals(obj is ItemStack other ? other : default);

        public override readonly int GetHashCode()
        {
            return HashCode.Combine(ItemID, Quantity, InstanceID);
        }

        public static bool operator ==(ItemStack a, ItemStack b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(ItemStack a, ItemStack b)
        {
            return !a.Equals(b);
        }
    }

    [Serializable]
    public class ItemInstanceData
    {
        public ItemID ItemID;
        public Guid InstanceID;

        public ItemInstanceData(ItemID itemID, Guid instanceID)
        {
            ItemID = itemID;
            InstanceID = instanceID;
        }
    }

    [Serializable]
    public class ToolInstanceData : ItemInstanceData
    {
        public int CurrentDurability;
        public int MaxDurability;

        public ToolInstanceData(ItemID itemID, Guid instanceID, int maxDurability)
            : base(itemID, instanceID)
        {
            MaxDurability = maxDurability;
            CurrentDurability = maxDurability;
        }
    }

}
