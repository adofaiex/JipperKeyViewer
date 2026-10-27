// Vertex sink abstraction / 顶点写入器抽象
//
// WHY / 缘起:
// uGUI's VertexHelper stores each vertex in EIGHT parallel List<T> (positions, colours,
// uv0..uv3, normals, tangents) and every AddVert writes all eight — even the 3-argument
// overload fills the streams you did not pass. Our geometry only ever uses position, colour
// and uv0. Measured on the real key-box workload (112 slots, 20px radius, 11312 vertices):
//
//     AddVert(UIVertex)                  0.6318 ms   (what this class used to do)
//     AddVert(Vector3, Color32, Vector4) 0.8577 ms   (NOT cheaper — still writes all 8 lists)
//     flat array write                   0.1168 ms   5.41x faster
//
// So the cost is the eight-List structure itself, not argument marshalling: the only way to
// collect it is to stop using VertexHelper. That is what MeshArraySink does.
//
// SAFETY / 安全性:
// Graphic.UpdateGeometry() chooses its path from useLegacyMeshGeneration:
//     if (useLegacyMeshGeneration) DoLegacyMeshGeneration(); else DoMeshGeneration();
// and DoLegacyMeshGeneration still runs IMeshModifier.ModifyMesh over the generated mesh and
// hands it to CanvasRenderer.SetMesh — masking, clipping and material resolution are all still
// performed by uGUI. This is NOT a bypass of the framework; it is the framework's other,
// still-supported path. (Verified by reading the IL of both methods.)
//
// uGUI 的 VertexHelper 把每个顶点存进**八条**并行 List（位置/颜色/uv0-3/法线/切线），每次
// AddVert 全部写一遍——连三参数重载也会补齐你没传的几条。而本项目几何只用到位置、颜色、uv0。
// 在真实按键框负载上实测（112 槽位、20px 半径、11312 顶点）：AddVert(UIVertex) 0.6318ms、
// 三参数重载 0.8577ms（并不更便宜，照样写满八条）、平铺数组 0.1168ms（快 5.41 倍）。差距来自
// 八条 List 这个结构本身，而非参数封送；要拿到它就必须不再用 VertexHelper，这正是
// MeshArraySink 做的事。
// 安全依据：Graphic.UpdateGeometry() 按 useLegacyMeshGeneration 选路径，而
// DoLegacyMeshGeneration **仍会执行 IMeshModifier.ModifyMesh** 并把结果交给
// CanvasRenderer.SetMesh——遮罩、裁剪与材质解析全部仍由 uGUI 完成。这不是绕过框架，而是走框架
// 另一条仍受支持的老路径（已通过阅读两个方法的 IL 证实）。

using UnityEngine;
using UnityEngine.UI;

namespace JipperKeyViewer.KeyViewer.Rendering
{
    /// <summary>The minimal surface the key-box geometry needs. Both the uGUI VertexHelper path
    /// and the flat-array Mesh path implement it, so the drawing code exists exactly once. /
    /// 按键框几何所需的最小接口。uGUI 的 VertexHelper 路径与平铺数组 Mesh 路径各实现一份，使
    /// 绘制代码只有一份。</summary>
    internal interface IKvMeshSink
    {
        /// <summary>Vertices accumulated so far; the drawing code uses it as a base index. /
        /// 目前已累积的顶点数；绘制代码用它作为基索引。</summary>
        int VertexCount { get; }

        void Clear();
        void AddVert(Vector3 position, Color color, Vector4 uv0);
        void AddTriangle(int a, int b, int c);
    }

    /// <summary>The original path, kept verbatim (UIVertex + AddVert) so the VertexHelper branch
    /// stays byte-for-byte what it always was. Used by the offline equivalence regression, and as
    /// the reference the Mesh path is compared against. / 原路径，逐字保留（UIVertex + AddVert），
    /// 使 VertexHelper 分支与从前**字节一致**。供离线等价性回归使用，并作为 Mesh 路径的比对基准。</summary>
    internal sealed class VertexHelperSink : IKvMeshSink
    {
        private readonly VertexHelper vh;

        public VertexHelperSink(VertexHelper vh) { this.vh = vh; }

        public int VertexCount => vh.currentVertCount;
        public void Clear() => vh.Clear();

        public void AddVert(Vector3 position, Color color, Vector4 uv0)
        {
            // Deliberately the same three assignments the old inline code did.
            // 刻意写成与旧内联代码相同的三行赋值。
            UIVertex vert = UIVertex.simpleVert;
            vert.position = position;
            vert.color = color;
            vert.uv0 = uv0;
            vh.AddVert(vert);
        }

        public void AddTriangle(int a, int b, int c) => vh.AddTriangle(a, b, c);
    }

    /// <summary>Fast path: three parallel managed arrays written straight into a Mesh.
    ///
    /// The arrays are owned by this sink and only ever GROW (doubling), so a rebuild allocates
    /// nothing. Finish() publishes just the used prefixes — passing the counts keeps the setters
    /// from uploading the whole, possibly much larger, backing array.
    ///
    /// Both this sink and VertexHelper end up copying into the native Mesh; the saving is that we
    /// copy 3 streams instead of 8, and no longer pay for the eight intermediate Lists.
    ///
    /// 快路径：三条并行托管数组直接写进 Mesh。数组由本写入器持有且**只增不减**（按两倍扩容），
    /// 故每次重建零分配。Finish() 只发布**已用前缀**——传入数量可避免把整块（可能大得多的）
    /// 后备数组上传。本写入器与 VertexHelper 最终都要拷进原生 Mesh；省下的是只拷 3 条流而非 8 条，
    /// 以及不再为那八条中间 List 付账。
    /// </summary>
    internal sealed class MeshArraySink : IKvMeshSink
    {
        private readonly Mesh mesh;
        private Vector3[] positions = new Vector3[256];
        private Color32[] colors = new Color32[256];
        private Vector4[] uv0s = new Vector4[256];
        private int[] indices = new int[768];
        private int vertCount;
        private int indexCount;

        public MeshArraySink(Mesh mesh) { this.mesh = mesh; }

        public int VertexCount => vertCount;

        public void Clear()
        {
            vertCount = 0;
            indexCount = 0;
        }

        public void AddVert(Vector3 position, Color color, Vector4 uv0)
        {
            if (vertCount == positions.Length)
            {
                int cap = positions.Length * 2;
                var p = new Vector3[cap];
                System.Array.Copy(positions, p, vertCount);
                positions = p;
                var c = new Color32[cap];
                System.Array.Copy(colors, c, vertCount);
                colors = c;
                var u = new Vector4[cap];
                System.Array.Copy(uv0s, u, vertCount);
                uv0s = u;
            }
            positions[vertCount] = position;
            // Color -> Color32 uses the same implicit conversion UIVertex.color performed, so the
            // bytes written match the VertexHelper path exactly (proven by the offline regression).
            // Color -> Color32 走的是 UIVertex.color 当初用的同一个隐式转换，故写入的字节与
            // VertexHelper 路径完全一致（已由离线回归证明）。
            colors[vertCount] = color;
            uv0s[vertCount] = uv0;
            vertCount++;
        }

        public void AddTriangle(int a, int b, int c)
        {
            if (indexCount + 3 > indices.Length)
            {
                var bigger = new int[indices.Length * 2];
                System.Array.Copy(indices, bigger, indexCount);
                indices = bigger;
            }
            indices[indexCount++] = a;
            indices[indexCount++] = b;
            indices[indexCount++] = c;
        }

        /// <summary>Publish the accumulated geometry. Only called by the Mesh entry point. /
        /// 发布累积的几何。只由 Mesh 入口调用。</summary>
        public void Finish()
        {
            // FillMesh calls the no-argument Clear() (keepVertexLayout defaults to true) and we
            // match it exactly — see the disassembly note on RecalculateBounds below.
            // FillMesh 调的是无参 Clear()（keepVertexLayout 默认 true），此处完全照做——见下方
            // RecalculateBounds 的反汇编说明。
            mesh.Clear();
            // uGUI's VertexHelper.FillMesh refuses to publish >= 65000 vertices (a 16-bit index
            // buffer), and the VertexHelper branch still goes through it. The Mesh branch does
            // NOT — DoLegacyMeshGeneration calls OnPopulateMesh(mesh) directly — so without this
            // guard a runaway geometry set would stop being a loud ArgumentException and become a
            // silently mishandled mesh. NOTE: legal documents can also reach this size — the old
            // "112 keys ≈ 29k" premise counted per GROUP — which is why KeyShapeLayer.DrawAll now
            // enforces its own budget gate and skips slots BEFORE finishing. With that gate in
            // place, this throw is reachable only by a genuine defect (e.g. a rain leak feeding
            // the shape layer) and stays deliberately loud. / uGUI 的 VertexHelper.FillMesh 拒绝
            // 发布 >= 65000 顶点（16 位索引缓冲），而 VertexHelper 分支仍经过它。Mesh 分支**不
            // 经过**——DoLegacyMeshGeneration 直接调 OnPopulateMesh(mesh)——故没有本护栏时，失控
            // 的几何量会从「响亮的 ArgumentException」变成「被静默错处理的 mesh」。注意：合法
            // 文档也可能达到这个规模——「112 键 ≈ 2.9 万」的旧前提是**按组**计数的——这正是
            // KeyShapeLayer.DrawAll 现在有了自己的预算闸、并在 Finish 之前跳过槽位的原因。闸就位
            // 后，本抛出只有真缺陷（如雨滴泄漏灌进形状层）才会触达，故刻意保持响亮。
            if (vertCount >= 65000)
                throw new System.ArgumentException("Mesh can not have more than 65000 vertices");
            if (vertCount == 0) return;
            mesh.SetVertices(positions, 0, vertCount);
            mesh.SetColors(colors, 0, vertCount);
            mesh.SetUVs(0, uv0s, 0, vertCount);
            mesh.SetTriangles(indices, 0, indexCount, 0, false);
            // FillMesh ends with this, and it is NOT optional: the mesh we write is uGUI's worker
            // mesh — shared, and its bounds describe whatever geometry was built into it LAST.
            // Publishing new vertices without new bounds leaves culling based on stale extents.
            // (Found by disassembling FillMesh, not by any test: bounds are an ECall and
            // unobservable offline, so only the reference implementation could reveal this.)
            // FillMesh 也以此收尾，且它**不是可选项**：我们写入的是 uGUI 的 worker mesh——它被共享，
            // 其 bounds 描述的是上一次建进它的几何。发布了新顶点却不更新 bounds，会让剔除依据
            // 过期的范围。（由反汇编 FillMesh 发现，而非任何测试——bounds 是 ECall，离线不可观测。）
            mesh.RecalculateBounds();
        }
    }
}
