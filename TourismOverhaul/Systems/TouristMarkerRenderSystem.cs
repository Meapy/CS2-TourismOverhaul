using Game;
using Game.Common;
using Game.Rendering;
using Game.Tools;
using TourismOverhaul.Components;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Draws a coloured ring on the ground under every entity marked by
    /// <see cref="TouristHighlightSystem"/>.
    ///
    /// Why not the native outline: Game.Tools.Highlighted does produce an outline, but its colour
    /// is not per-entity. BatchDataSystem.cs:768 resolves the _Outlines_Color shader property from
    /// the five global colours in RenderingSettingsData —
    ///     Error -> m_ErrorColor, Warning -> m_WarningColor, Override -> m_OverrideColor,
    ///     otherwise -> m_HoveredColor
    /// so a plain Highlighted always paints the hover colour, and changing it would recolour hover
    /// for the entire game.
    ///
    /// OverlayRenderSystem.Buffer.DrawCircle takes an arbitrary colour per call
    /// (OverlayRenderSystem.cs:127), which is how tools and info views draw. That gives a fully
    /// configurable colour and a look that is clearly distinct from a hover highlight.
    ///
    /// Registered with UpdateBefore&lt;_, OverlayRenderSystem&gt; so the buffer is filled before it
    /// is consumed.
    /// </summary>
    public partial class TouristMarkerRenderSystem : GameSystemBase
    {
        private EntityQuery m_MarkedQuery;
        private OverlayRenderSystem m_OverlayRenderSystem;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();

            m_MarkedQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristOutline>(),
                ComponentType.ReadOnly<Game.Objects.Transform>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            RequireForUpdate(m_MarkedQuery);
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;
            if (settings == null || !settings.HighlightTourists)
            {
                return;
            }

            Color outlineColor = settings.GetMarkerColor();
            Color fillColor = outlineColor;
            fillColor.a *= 0.25f;

            float diameter = math.max(0.5f, settings.MarkerSize);
            float outlineWidth = math.max(0.05f, diameter * 0.12f);

            OverlayRenderSystem.Buffer buffer = m_OverlayRenderSystem.GetBuffer(out JobHandle bufferDeps);
            bufferDeps.Complete();

            ComponentTypeHandle<Game.Objects.Transform> transformHandle =
                GetComponentTypeHandle<Game.Objects.Transform>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_MarkedQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    NativeArray<Game.Objects.Transform> transforms = chunk.GetNativeArray(ref transformHandle);

                    for (int i = 0; i < transforms.Length; i++)
                    {
                        buffer.DrawCircle(
                            outlineColor,
                            fillColor,
                            outlineWidth,
                            OverlayRenderSystem.StyleFlags.Projected,
                            new float2(0f, 1f),
                            transforms[i].m_Position,
                            diameter);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            m_OverlayRenderSystem.AddBufferWriter(default(JobHandle));
        }
    }
}
