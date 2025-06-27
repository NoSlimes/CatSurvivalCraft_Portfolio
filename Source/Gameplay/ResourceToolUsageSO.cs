using NoSlimes.Gameplay.Util.Extensions;
using NoSlimes.Logging;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.VisualScripting;
using UnityEngine;

namespace NoSlimes.Gameplay
{
    [CreateAssetMenu(fileName = "ResourceToolItemUsage", menuName = "NoSlimes/Item/ToolItemUsage")]
    public class ResourceToolUsageSO : ItemUsageSO
    {
        [SerializeField] private ToolType toolType = ToolType.None;

        protected override void UseInternal(NetworkObject user, ItemData data, ItemInstanceData instanceData, ItemUsageContext context)
        {
            if (data is not ToolData toolData)
            {
                DLog.DevLogError("Invalid item data provided for tool usage.", this);
                return;
            }

            if (user.TryGetComponentInChildren(out NetworkAnimator anim))
                anim.SetTrigger("Attack");

            DLog.DevLog($"Using tool item: {data.Name} by user: {user.name}", this);

            if (Physics.Raycast(context.OriginPosition, context.AimDirection, out RaycastHit hit, toolData.Range))
            {
                if (!hit.collider.TryGetComponent<ResourceNode>(out var resourceNode))
                    return;

                if (resourceNode.RequiredToolType != toolType)
                {
                    DLog.DevLog($"Tool type {toolType} is not suitable for this resource node.", this);
                    return;
                }

                DamageEventData damageEventData = new(toolData.Damage, user.OwnerClientId);
                resourceNode.ApplyDamage(damageEventData);
            }
        }
    }
}
