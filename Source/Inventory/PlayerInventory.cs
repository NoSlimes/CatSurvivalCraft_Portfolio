using NoSlimes.Logging;
using NUnit.Framework.Interfaces;
using System;
using Unity.Netcode;
using UnityEngine;

namespace NoSlimes.Gameplay
{
    public class PlayerInventory : Inventory
    {
        [SerializeField] private int defaultHotbarSlots = 5;
        private readonly NetworkVariable<int> additionalHotbarSlots = new(0);

        public int HotbarSlotCount => defaultHotbarSlots + additionalHotbarSlots.Value;
        public override int SlotCount => base.SlotCount + HotbarSlotCount;

        #region Item Management
        protected override bool AddItemInternal(ItemID item, ushort quantity, int stackSize)
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
                for (int i = HotbarSlotCount; i < inventoryItems.Count && remaining > 0; i++)
                {
                    ItemStack slot = inventoryItems[i];
                    if (slot.ItemID == item && !slot.HasInstanceData && slot.Quantity < stackSize)
                    {
                        ushort space = (ushort)(stackSize - slot.Quantity);
                        ushort toAdd = (ushort)Mathf.Min(space, remaining);
                        if (toAdd > 0)
                        {
                            slot.Quantity += toAdd;
                            inventoryItems[i] = slot;
                            remaining -= toAdd;
                            DLog.DevLog($"Stacked {toAdd} of {item.ID} into inventory slot {i}.", this, LogInventory);
                        }
                    }
                }

                for (int i = 0; i < HotbarSlotCount && remaining > 0; i++)
                {
                    ItemStack slot = inventoryItems[i];
                    if (slot.ItemID == item && !slot.HasInstanceData && slot.Quantity < stackSize)
                    {
                        ushort space = (ushort)(stackSize - slot.Quantity);
                        ushort toAdd = (ushort)Mathf.Min(space, remaining);
                        if (toAdd > 0)
                        {
                            slot.Quantity += toAdd;
                            inventoryItems[i] = slot;
                            remaining -= toAdd;
                            DLog.DevLog($"Stacked {toAdd} of {item.ID} into hotbar slot {i}.", this, LogInventory);
                        }
                    }
                }
            }

            for (int i = HotbarSlotCount; i < inventoryItems.Count && remaining > 0; i++)
            {
                if (inventoryItems[i].ItemID == ItemID.INVALID_ID)
                {
                    Guid newInstanceId = CreateItemInstance(itemData);

                    if (newInstanceId != Guid.Empty)
                    {
                        inventoryItems[i] = new ItemStack { ItemID = item, Quantity = 1, InstanceID = newInstanceId };
                        remaining--;
                        DLog.DevLog($"Added INSTANCED item {item.ID} to new inventory slot {i}.", this, LogInventory);
                    }
                    else
                    {
                        ushort toAdd = (ushort)Mathf.Min(stackSize, remaining);
                        inventoryItems[i] = new ItemStack { ItemID = item, Quantity = toAdd, InstanceID = Guid.Empty };
                        remaining -= toAdd;
                        DLog.DevLog($"Added {toAdd} of STACKABLE item {item.ID} to new inventory slot {i}.", this, LogInventory);
                    }
                }
            }

            for (int i = 0; i < HotbarSlotCount && remaining > 0; i++)
            {
                if (inventoryItems[i].ItemID == ItemID.INVALID_ID)
                {
                    Guid newInstanceId = CreateItemInstance(itemData);

                    if (newInstanceId != Guid.Empty)
                    {
                        inventoryItems[i] = new ItemStack { ItemID = item, Quantity = 1, InstanceID = newInstanceId };
                        remaining--;
                        DLog.DevLog($"Added INSTANCED item {item.ID} to new hotbar slot {i}.", this, LogInventory);
                    }
                    else
                    {
                        ushort toAdd = (ushort)Mathf.Min(stackSize, remaining);
                        inventoryItems[i] = new ItemStack { ItemID = item, Quantity = toAdd, InstanceID = Guid.Empty };
                        remaining -= toAdd;
                        DLog.DevLog($"Added {toAdd} of STACKABLE item {item.ID} to new hotbar slot {i}.", this, LogInventory);
                    }
                }
            }

            NotifyClientsOfChange();
            return remaining == 0;
        }

        protected override bool RemoveItemInternal(ItemID item, ushort quantity)
        {
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

            ushort remainingToRemove = quantity;

            // Remove from hotbar slots first
            for (int i = 0; i < HotbarSlotCount && remainingToRemove > 0; i++)
            {
                ItemStack slot = inventoryItems[i];
                if (slot.ItemID == item)
                {
                    if (slot.Quantity <= remainingToRemove)
                    {
                        remainingToRemove -= slot.Quantity;
                        slot.ItemID = ItemID.INVALID_ID;
                        slot.Quantity = 0;

                        if (slot.HasInstanceData)
                            instanceDataMap.Remove(slot.InstanceID);
                    }
                    else
                    {
                        slot.Quantity -= remainingToRemove;
                        remainingToRemove = 0;
                    }

                    inventoryItems[i] = slot;

                    DLog.DevLog($"Removed from hotbar slot {i}. Remaining to remove: {remainingToRemove}", this, LogInventory);
                }
            }

            // Remove from inventory slots
            for (int i = HotbarSlotCount; i < inventoryItems.Count && remainingToRemove > 0; i++)
            {
                ItemStack slot = inventoryItems[i];
                if (slot.ItemID == item)
                {
                    if (slot.Quantity <= remainingToRemove)
                    {
                        remainingToRemove -= slot.Quantity;
                        slot.ItemID = ItemID.INVALID_ID;
                        slot.Quantity = 0;

                        if (slot.HasInstanceData)
                            instanceDataMap.Remove(slot.InstanceID);
                    }
                    else
                    {
                        slot.Quantity -= remainingToRemove;
                        remainingToRemove = 0;
                    }

                    inventoryItems[i] = slot;

                    DLog.DevLog($"Removed from inventory slot {i}. Remaining to remove: {remainingToRemove}", this, LogInventory);
                }
            }

            NotifyClientsOfChange();
            return true;
        }
        #endregion

        /// <summary>
        /// Returns a copy of the hotbar slots only (first HotbarSlotCount slots).
        /// </summary>
        public ItemStack[] GetHotbarItems()
        {
            ItemStack[] items = GetItems(ignoreServerRestriction: true); //HERE

            int count = HotbarSlotCount;
            if (items.Length < count)
            {
                DLog.DevLogWarning($"InventoryItems count ({items.Length}) is less than HotbarSlotCount ({count}). Returning empty array.", this, LogInventory);
                return Array.Empty<ItemStack>();
            }

            ItemStack[] hotbar = new ItemStack[count];
            for (int i = 0; i < count; i++)
            {
                hotbar[i] = items[i];
            }
            return hotbar;
        }

        /// <summary>
        /// Returns a copy of the normal inventory slots only (slots after hotbar).
        /// </summary>
        public ItemStack[] GetInventoryItems()
        {
            ItemStack[] items = GetItems();

            int count = items.Length - HotbarSlotCount;
            if (count < 0)
            {
                // Log error or warning to debug
                DLog.DevLogWarning($"InventoryItems count ({items.Length}) is less than HotbarSlotCount ({HotbarSlotCount}). Returning empty array.", this, LogInventory);
                return Array.Empty<ItemStack>();
            }

            ItemStack[] inventory = new ItemStack[count];
            for (int i = 0; i < count; i++)
            {
                inventory[i] = items[i + HotbarSlotCount];
            }
            return inventory;
        }
    }
}
