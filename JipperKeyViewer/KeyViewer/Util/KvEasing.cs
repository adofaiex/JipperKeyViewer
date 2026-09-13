// Named easing functions for press / counter animations / 按压 / 计数动画的命名缓动函数
// The standard Penner set plus the two polynomial smoothsteps, by name. Names are the persisted
// form, so an unknown or misspelled name must degrade to linear rather than throw — a profile
// written by a newer build (or hand-edited) has to stay loadable.
// 标准 Penner 集合加两个多项式 smoothstep，按名字取用。名字即持久化形式，故未知或拼错的名字必须
// 退化为 linear 而非抛异常——更新版本写出的（或手改的）配置必须仍可加载。

using System;
using UnityEngine;

namespace JipperKeyViewer.KeyViewer.Util
{
    public static class KvEasing
    {
        /// <summary>Every selectable easing, in menu order. / 全部可选缓动，按菜单顺序。</summary>
        public static readonly string[] Names =
        {
            "linear", "smoothstep", "smootherstep",
            "ease-in-sine", "ease-out-sine", "ease-in-out-sine",
            "ease-in-quad", "ease-out-quad", "ease-in-out-quad",
            "ease-in-cubic", "ease-out-cubic", "ease-in-out-cubic",
            "ease-in-quart", "ease-out-quart", "ease-in-out-quart",
            "ease-in-quint", "ease-out-quint", "ease-in-out-quint",
            "ease-in-expo", "ease-out-expo", "ease-in-out-expo",
            "ease-in-circ", "ease-out-circ", "ease-in-out-circ",
            "ease-in-back", "ease-out-back", "ease-in-out-back",
        };

        public const string Default = "ease-out-cubic";

        /// <summary>Index of a name in <see cref="Names"/>, or 0 (linear) when unknown — the combo
        /// boxes are index-driven, so an unrecognized stored name must land somewhere valid. /
        /// 名字在 <see cref="Names"/> 中的下标；未知时返回 0（linear）——下拉框按下标工作，故无法
        /// 识别的存储名必须落到一个有效项上。</summary>
        public static int IndexOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            for (int i = 0; i < Names.Length; i++)
                if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        /// <summary>Normalize a stored name to a known one (empty/unknown → linear). / 把存储名规范化
        /// 为已知名（空/未知 → linear）。</summary>
        public static string Normalize(string name) => Names[IndexOf(name)];

        /// <summary>Apply the named easing. t is clamped to [0,1]; the result may leave [0,1] for the
        /// back variants (that overshoot IS the effect). / 应用命名缓动。t 钳制到 [0,1]；back 系列
        /// 的结果可越出 [0,1]（那一下过冲正是效果本身）。</summary>
        public static float Ease(string name, float t)
        {
            t = Mathf.Clamp01(t);
            switch (Normalize(name))
            {
                case "smoothstep": return t * t * (3f - 2f * t);
                case "smootherstep": return t * t * t * (t * (t * 6f - 15f) + 10f);

                case "ease-in-sine": return 1f - Mathf.Cos(t * Mathf.PI * 0.5f);
                case "ease-out-sine": return Mathf.Sin(t * Mathf.PI * 0.5f);
                case "ease-in-out-sine": return -(Mathf.Cos(Mathf.PI * t) - 1f) * 0.5f;

                case "ease-in-quad": return t * t;
                case "ease-out-quad": return 1f - (1f - t) * (1f - t);
                case "ease-in-out-quad": return t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) * 0.5f;

                case "ease-in-cubic": return t * t * t;
                case "ease-out-cubic": return 1f - Mathf.Pow(1f - t, 3f);
                case "ease-in-out-cubic": return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;

                case "ease-in-quart": return t * t * t * t;
                case "ease-out-quart": return 1f - Mathf.Pow(1f - t, 4f);
                case "ease-in-out-quart": return t < 0.5f ? 8f * t * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 4f) * 0.5f;

                case "ease-in-quint": return t * t * t * t * t;
                case "ease-out-quint": return 1f - Mathf.Pow(1f - t, 5f);
                case "ease-in-out-quint": return t < 0.5f ? 16f * t * t * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 5f) * 0.5f;

                case "ease-in-expo": return t <= 0f ? 0f : Mathf.Pow(2f, 10f * t - 10f);
                case "ease-out-expo": return t >= 1f ? 1f : 1f - Mathf.Pow(2f, -10f * t);
                // The 1e-10 guards keep the in-out branches from returning 0.0009 at t=0 / 0.9991 at
                // t=1 — an animation that never quite starts or finishes is visibly wrong.
                // 1e-10 的守卫让 in-out 分支在 t=0 / t=1 时不会返回 0.0009 / 0.9991——一个永远
                // 差一点没开始或没结束的动画是肉眼可见的错。
                case "ease-in-out-expo":
                    if (t <= 0f) return 0f;
                    if (t >= 1f) return 1f;
                    return t < 0.5f
                        ? Mathf.Pow(2f, 20f * t - 10f) * 0.5f
                        : (2f - Mathf.Pow(2f, -20f * t + 10f)) * 0.5f;

                case "ease-in-circ": return 1f - Mathf.Sqrt(Mathf.Max(0f, 1f - t * t));
                case "ease-out-circ": return Mathf.Sqrt(Mathf.Max(0f, 1f - (t - 1f) * (t - 1f)));
                case "ease-in-out-circ":
                    if (t < 0.5f) return (1f - Mathf.Sqrt(Mathf.Max(0f, 1f - 4f * t * t))) * 0.5f;
                    return (Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Pow(-2f * t + 2f, 2f))) + 1f) * 0.5f;

                case "ease-in-back": return Back(t, 1.70158f, false);
                case "ease-out-back": return Back(t, 1.70158f, true);
                case "ease-in-out-back": return BackInOut(t);

                default: return t; // "linear" and anything unrecognized / "linear" 及任何无法识别项
            }
        }

        private static float Back(float t, float c1, bool outwards)
        {
            const float c3 = 1.70158f + 1f;
            if (!outwards) return c3 * t * t * t - 1.70158f * t * t;
            float p = t - 1f;
            return 1f + c3 * p * p * p + 1.70158f * p * p;
        }

        private static float BackInOut(float t)
        {
            const float c2 = 1.70158f * 1.525f;
            if (t < 0.5f)
            {
                float p = 2f * t;
                return p * p * ((c2 + 1f) * p - c2) * 0.5f;
            }
            float q = 2f * t - 2f;
            return (q * q * ((c2 + 1f) * q + c2) + 2f) * 0.5f;
        }

        /// <summary>Sample the curve for a preview graph: values in [0,1] over x in [0,1]. Back
        /// easings overshoot, so the caller must map the range itself. / 为预览曲线取样：x 取 [0,1]，
        /// 值在 [0,1]。back 系列会过冲，故调用方需自行映射范围。</summary>
        public static void SampleCurve(string name, Vector2[] points)
        {
            if (points == null || points.Length == 0) return;
            int last = points.Length - 1;
            for (int i = 0; i <= last; i++)
            {
                float x = last == 0 ? 0f : i / (float)last;
                points[i] = new Vector2(x, Ease(name, x));
            }
        }
    }
}