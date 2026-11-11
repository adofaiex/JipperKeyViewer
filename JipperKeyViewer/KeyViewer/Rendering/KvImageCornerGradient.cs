// Four-corner gradient tint for FreeMake image/video nodes / FreeMake 图片/视频节点的四角渐变染色
// A BaseMeshEffect on the node's RawImage: multiplies every vertex colour by the bilinear
// interpolation of the four corner colours across the rect. Null/invalid corner arrays degrade
// to white (no-op), matching how the text corner gradient treats missing data. The component is
// attached in CreateCustomImageObject and updated in place by the editor's colour-commit path —
// no overlay rebuild, no extra draw call (uGUI batches RawImage + mesh effect as before).
// 挂在节点 RawImage 上的 BaseMeshEffect：把每个顶点色乘以四角色在矩形内的双线性插值。
// null/非法的角色数组退化为白色（无操作），与文字四角渐变对缺失数据的处理一致。组件在
// CreateCustomImageObject 挂载、由编辑器取色提交路径就地更新——不重建覆盖层、不增加绘制批次。

using UnityEngine;
using UnityEngine.UI;

namespace JipperKeyViewer.KeyViewer.Rendering
{
    public class KvImageCornerGradient : BaseMeshEffect
    {
        private Color topLeft = Color.white;
        private Color topRight = Color.white;
        private Color bottomLeft = Color.white;
        private Color bottomRight = Color.white;

        /// <summary>Update all four corners at once and repaint. Non-finite components fall
        /// back to white per channel — corner colors come from user-editable JSON. /
        /// 一次性更新四角并重绘。非有限分量逐通道回退白色——角色来自用户可编辑的 JSON。</summary>
        public void SetCorners(Color tl, Color tr, Color bl, Color br)
        {
            topLeft = SafeCorner(tl);
            topRight = SafeCorner(tr);
            bottomLeft = SafeCorner(bl);
            bottomRight = SafeCorner(br);
            if (graphic != null) graphic.SetVerticesDirty();
        }

        private static Color SafeCorner(Color c)
        {
            if (float.IsNaN(c.r) || float.IsInfinity(c.r)) c.r = 1f;
            if (float.IsNaN(c.g) || float.IsInfinity(c.g)) c.g = 1f;
            if (float.IsNaN(c.b) || float.IsInfinity(c.b)) c.b = 1f;
            if (float.IsNaN(c.a) || float.IsInfinity(c.a)) c.a = 1f;
            return c;
        }

        public override void ModifyMesh(UnityEngine.UI.VertexHelper vh)
        {
            if (!isActiveAndEnabled || vh == null) return;
            if (!enabled) return;
            // All-white is the no-op case (missing arrays / user picked white everywhere):
            // skipping the vertex walk avoids a pointless O(n) pass on every rebuild. /
            // 全白即无操作情形（数组缺失/用户全选白）：跳过顶点遍历，避免每次重建都做
            // 无谓的 O(n) 扫描。
            if (topLeft == Color.white && topRight == Color.white
                && bottomLeft == Color.white && bottomRight == Color.white) return;
            if (vh.currentVertCount == 0) return;
            Rect rect = graphic != null && graphic.rectTransform != null
                ? graphic.rectTransform.rect
                : new Rect(0f, 0f, 1f, 1f);
            float w = rect.width, h = rect.height;
            if (w <= 0f || h <= 0f) return;
            UIVertex v = default;
            int count = vh.currentVertCount;
            for (int i = 0; i < count; i++)
            {
                vh.PopulateUIVertex(ref v, i);
                float tx = Mathf.Clamp01((v.position.x - rect.x) / w);
                float ty = Mathf.Clamp01((v.position.y - rect.y) / h);
                // Bilerp over the rect: bottom row first, then vertical lerp — same corner
                // convention as the text corner gradient (TopLeft/TopRight/BottomLeft/BottomRight).
                // 矩形内双线性插值：先底行再纵向——与文字四角渐变同一套角点约定。
                Color bottom = Color.Lerp(bottomLeft, bottomRight, tx);
                Color top = Color.Lerp(topLeft, topRight, tx);
                Color c = Color.Lerp(bottom, top, ty);
                v.color *= c;
                vh.SetUIVertex(v, i);
            }
        }
    }
}
