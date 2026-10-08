using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Spine.Interfaces
{
    public interface ISkeleton
    {
        /// <summary>
        /// 物理约束
        /// </summary>
        public enum Physics { None, Reset, Update, Pose }

        /// <summary>
        /// 名称
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// R
        /// </summary>
        public float R { get; set; }

        /// <summary>
        /// G
        /// </summary>
        public float G { get; set; }

        /// <summary>
        /// B
        /// </summary>
        public float B { get; set; }

        /// <summary>
        /// A
        /// </summary>
        public float A { get; set; }

        /// <summary>
        /// 横坐标
        /// </summary>
        public float X { get; set; }

        /// <summary>
        /// 纵坐标
        /// </summary>
        public float Y { get; set; }

        /// <summary>
        /// 水平缩放, 负数时会翻转
        /// </summary>
        public float ScaleX { get; set; }

        /// <summary>
        /// 垂直缩放, 负数时会翻转
        /// </summary>
        public float ScaleY { get; set; }

        /// <summary>
        /// 所有骨骼按顺序
        /// </summary>
        public ImmutableArray<IBone> Bones { get; }
        
        /// <summary>
        /// 所有骨骼按名称
        /// </summary>
        public FrozenDictionary<string, IBone> BonesByName { get; }

        /// <summary>
        /// 所有插槽按顺序
        /// </summary>
        public ImmutableArray<ISlot> Slots { get; }

        /// <summary>
        /// 所有插槽按名称
        /// </summary>
        public FrozenDictionary<string, ISlot> SlotsByName { get; }

        /// <summary>
        /// 皮肤, 可以设置 null 值清除皮肤
        /// </summary>
        public ISkin? Skin { get; set; }

        /// <summary>
        /// 遍历渲染时槽顺序
        /// </summary>
        public IEnumerable<ISlot> IterDrawOrder();

        /// <summary>
        /// 更新附件约束等的缓存
        /// </summary>
        public void UpdateCache();

        /// <summary>
        /// 更新变换
        /// </summary>
        public void UpdateWorldTransform(Physics physics);

        /// <summary>
        /// 重置所有参数至初始值
        /// </summary>
        public void SetToSetupPose();

        /// <summary>
        /// 重置所有骨骼参数至初始值
        /// </summary>
        public void SetBonesToSetupPose();

        /// <summary>
        /// 重置所有插槽参数至初始值
        /// </summary>
        public void SetSlotsToSetupPose();

        /// <summary>
        /// 列出插槽在当前皮肤与 default 皮肤里可用的全部附件名 (皮肤里登记的键名, 即 SetAttachment 用的名字)
        /// </summary>
        public IReadOnlyList<string> GetSlotAttachmentNames(int slotIndex);

        /// <summary>
        /// 设置插槽附件, <paramref name="attachmentName"/> 为 <c>null</c> 时清空插槽
        /// </summary>
        /// <returns>插槽或附件不存在时返回 false 且不做修改</returns>
        public bool SetAttachment(string slotName, string? attachmentName);

        /// <summary>
        /// 更新时间
        /// </summary>
        public void Update(float delta);
    }
}
