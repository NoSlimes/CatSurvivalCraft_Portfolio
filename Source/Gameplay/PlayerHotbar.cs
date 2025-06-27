using NoSlimes.Logging;
using System;
using Unity.Netcode;
using Unity.VisualScripting;
using UnityEngine;

namespace NoSlimes.Gameplay
{
    public class PlayerHotbar : NetworkBehaviour
    {
        [SerializeField] private Transform cameraTargetTransform;

        private PlayerInventory inventory;

        private readonly NetworkVariable<int> selectedIndex = new(0);

        public PlayerInventory Inventory => inventory;
        public int SelectedIndex
        {
            get => selectedIndex.Value;
            set
            {
                int clamped = Mathf.Clamp(value, 0, inventory.HotbarSlotCount - 1);
                selectedIndex.Value = clamped;
            }
        }

        public event Action<int> OnSelectedIndexChanged;

        private void Awake()
        {
            inventory = GetComponent<PlayerInventory>();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (!IsOwner)
            {
                enabled = false;
                return;
            }

            InputManager.Instance.RegisterActionCallback(ActionNames.HotbarScroll, OnHotbarScroll);
            InputManager.Instance.RegisterActionCallback(ActionNames.HotbarSelect, OnHotbarSelect);

            InputManager.Instance.RegisterActionCallback(ActionNames.Attack, OnInputAttack);
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            if (!IsOwner || InputManager.Instance == null)
                return;

            InputManager.Instance.UnregisterActionCallback(ActionNames.HotbarScroll, OnHotbarScroll);
            InputManager.Instance.UnregisterActionCallback(ActionNames.HotbarSelect, OnHotbarSelect);

            InputManager.Instance.UnregisterActionCallback(ActionNames.Attack, OnInputAttack);
        }

        private void OnEnable()
        {
            inventory.OnInventoryItemsChanged += HandleInventoryItemsChanged;
            selectedIndex.OnValueChanged += HandleSelectedIndexChanged;
        }

        private void OnDisable()
        {
            inventory.OnInventoryItemsChanged -= HandleInventoryItemsChanged;
            selectedIndex.OnValueChanged -= HandleSelectedIndexChanged;
        }

        private void HandleInventoryItemsChanged()
        {
            SetSelectedIndexServerRpc(SelectedIndex);
        }

        private void HandleSelectedIndexChanged(int previousValue, int newValue)
        {
            OnSelectedIndexChanged?.Invoke(newValue);
        }

        private void OnHotbarScroll(UnityEngine.InputSystem.InputAction.CallbackContext context)
        {
            if (context.performed)
            {
                float scrollValue = context.ReadValue<float>();
                if (scrollValue > 0)
                {
                    Scroll(1);
                }
                else if (scrollValue < 0)
                {
                    Scroll(-1);
                }
            }
        }

        private void OnHotbarSelect(UnityEngine.InputSystem.InputAction.CallbackContext context)
        {
            if (context.performed)
            {
                int newIndex = (int)context.ReadValue<float>();
                SetSelectedIndexServerRpc(newIndex);
            }
        }

        private void OnInputAttack(UnityEngine.InputSystem.InputAction.CallbackContext context)
        {
            ItemStack? selectedItem = GetSelectedSlot();
            if (!(selectedItem.HasValue && selectedItem.Value.ItemID != ItemID.INVALID_ID))
                return; //Item stack is empty or invalid

            if (ItemDatabaseSO.Instance.GetItemByID(selectedItem.Value.ItemID) is ToolData toolData && toolData.ToolUsage != null)
            {
                toolData.ToolUsage.TriggerOwnerEffects(this.gameObject);
            }

            UseItemServerRpc();
        }

        [Rpc(SendTo.Server)]
        private void UseItemServerRpc()
        {
            if (GetSelectedSlot() is not ItemStack selectedItem || selectedItem.ItemID == ItemID.INVALID_ID)
            {
                DLog.DevLogWarning("No valid item selected to use.");
                return;
            }

            UseItem(selectedItem);
            TriggerReplicatedEffectsClientRpc(selectedItem.ItemID);
        }

        [Rpc(SendTo.Everyone)]
        private void TriggerReplicatedEffectsClientRpc(ItemID item, RpcParams rpcParams = default)
        {
            ItemUsageSO itemUsage = (ItemDatabaseSO.Instance.GetItemByID(item) as ToolData)?.ToolUsage;

            if (itemUsage == null)
            {
                DLog.DevLogWarning($"No ToolUsage defined for item {item}.");
                return;
            }

            if (!IsOwner)
            {
                itemUsage.TriggerReplicatedEffects(gameObject);
            }
            else
            {

            }
        }

        private void UseItem(ItemStack itemStack)
        {
            if (cameraTargetTransform == null)
            {
                DLog.DevLogError("Camera target transform is not set. Cannot use item without camera reference.");
                return;
            }

            if (!IsServer)
            {
                DLog.DevLogError("UseItem can only be called on the server.");
                return;
            }

            if (ItemDatabaseSO.Instance.GetItemByID(itemStack.ItemID) is not ToolData toolData)
            {
                DLog.DevLogWarning($"Selected item {itemStack.ItemID} is not a ToolData item.");
                return;
            }

            if (!itemStack.HasInstanceData)
            {
                DLog.DevLogWarning($"Item {toolData.Name} does not have instance data. Cannot use item without instance data.");
                return;
            }

            var instanceData = Inventory.GetInstanceData(itemStack.InstanceID);
            if (instanceData == null)
            {
                DLog.DevLogError($"No instance data found for item with InstanceID {itemStack.InstanceID}.");
                return;
            }

            if (toolData.ToolUsage == null)
            {
                DLog.DevLogWarning($"ToolData for item {toolData.Name} does not have a ToolUsage defined.");
                return;
            }

            toolData.ToolUsage.Use(NetworkObject, toolData, instanceData, new ItemUsageContext(cameraTargetTransform.forward, cameraTargetTransform.position));
        }

        public ItemStack? GetSelectedSlot()
        {
            ItemStack[] hotbar = inventory.GetHotbarItems();
            if (hotbar.Length == 0 || SelectedIndex >= hotbar.Length)
                return null;

            return hotbar[SelectedIndex];
        }

        [Rpc(SendTo.Server)]
        public void SetSelectedIndexServerRpc(int newIndex, RpcParams rpcParams = default)
        {
            SetSelectedIndexInternal(newIndex);
        }

        private void SetSelectedIndexInternal(int newIndex)
        {
            if (!IsServer)
            {
                DLog.DevLogWarning("SetSelectedIndexInternal can only be called on the server.");
                return;
            }

            int clamped = Mathf.Clamp(newIndex, 0, inventory.HotbarSlotCount - 1);
            selectedIndex.Value = clamped;

            ItemStack? selectedItem = GetSelectedSlot();

            if (selectedItem.HasValue)
                SpawnSelectedItemClientRpc(selectedItem.Value);
        }

        public void Scroll(int direction)
        {
            int newIndex = (SelectedIndex + direction + inventory.HotbarSlotCount) % inventory.HotbarSlotCount;
            SetSelectedIndexServerRpc(newIndex);
        }

        #region test functions :D
        GameObject selectedItemObj;

        [Rpc(SendTo.Everyone)]
        private void SpawnSelectedItemClientRpc(ItemStack selectedItem)
        {
            if (selectedItemObj != null)
            {
                Destroy(selectedItemObj);
                selectedItemObj = null;
            }

            if (selectedItem.ItemID != ItemID.INVALID_ID)
            {
                ToolData toolData = (ItemDatabaseSO.Instance.GetItemByID(selectedItem.ItemID) as ToolData);
                if (toolData != null)
                {
                    if (toolData.Prefab == null)
                        return;

                    Transform socket = GetComponent<SocketManager>().GetSocket("Socket_RightHand");
                    if (socket == null)
                    {
                        DLog.DevLogWarning("No socket found for spawning item.");
                        return;
                    }

                    selectedItemObj = Instantiate(toolData.Prefab, socket);
                }
                else
                {
                    DLog.DevLogWarning($"No ToolData found for ItemID {selectedItem.ItemID}");
                }
            }
        }
        #endregion
    }
}
