using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Adds a Tourism Finance info view next to the game's Tourism view.
    ///
    /// Built the same way the hotel zones are: find the stock Tourism view, copy what makes it work,
    /// and change only what should differ. That keeps the new view consistent with the game's own
    /// and means it inherits anything a future update adds.
    ///
    /// The infomodes are deliberately shared with the Tourism view rather than duplicated.
    /// InfoviewPrefab.isValid is false unless m_Infomodes has entries (:67), so a view with none
    /// would never appear, and the map colouring a player wants while reading tourist finances is
    /// the same colouring they want while reading tourist numbers: hotels, attractions and where
    /// the visitors are. Infomodes are referenced rather than owned, so sharing them is how the
    /// game already expects them to be used.
    ///
    /// Priority places it immediately after Tourism in the toolbar.
    /// </summary>
    public partial class TourismFinanceViewSystem : GameSystemBase
    {
        private const string kViewName = "TourismOverhaul Finance";

        /// <summary>The stock view this one is modelled on and sits beside.</summary>
        private const string kTemplateName = "Tourism";

        /// <summary>
        /// Served from the mod's own UI folder, like the zone icons. Drawn to match the game's
        /// Tourism.svg — same projection, same palette — with a currency mark added.
        /// </summary>
        private const string kIconPath = "coui://ui-mods/images/tourism-finance.svg";

        private PrefabSystem m_PrefabSystem;
        private bool m_Created;

        // The work happens once, during load.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 4096;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);

            // Info views are read during load, so this has to exist before then.
            CreateView();
        }

        protected override void OnUpdate()
        {
            // Fallback, for the case where the template was not available during preload.
            if (!m_Created)
            {
                CreateView();
            }
        }

        private void CreateView()
        {
            if (m_Created)
            {
                return;
            }

            InfoviewPrefab template = FindTemplate();

            if (template == null)
            {
                Mod.Log.Warn(
                    $"No \"{kTemplateName}\" info view found; the finance view is unavailable. " +
                    "It has probably been renamed by a game update.");
                return;
            }

            try
            {
                InfoviewPrefab view = ScriptableObject.CreateInstance<InfoviewPrefab>();

                view.name = kViewName;
                view.m_IconPath = kIconPath;
                view.m_Group = template.m_Group;

                // Immediately after Tourism, so the pair sit together.
                view.m_Priority = template.m_Priority + 1;

                view.m_DefaultColor = template.m_DefaultColor;
                view.m_SecondaryColor = template.m_SecondaryColor;
                view.m_WarningCategories = template.m_WarningCategories;
                view.m_Editor = false;

                // Shared, not copied. Without at least one the view is never valid (:67).
                view.m_Infomodes = template.m_Infomodes;

                if (!m_PrefabSystem.AddPrefab(view))
                {
                    Mod.Log.Warn($"PrefabSystem rejected the \"{kViewName}\" info view.");
                    return;
                }

                m_Created = true;

                Mod.Log.Info(
                    $"Created info view \"{kViewName}\" beside \"{kTemplateName}\" " +
                    $"(priority {view.m_Priority}, {view.m_Infomodes?.Length ?? 0} infomode(s)).");
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not create the \"{kViewName}\" info view: {e.Message}");
            }
        }

        /// <summary>The stock Tourism view, found by name among the loaded info views.</summary>
        private InfoviewPrefab FindTemplate()
        {
            EntityQuery query = GetEntityQuery(
                ComponentType.ReadOnly<InfoviewData>(),
                ComponentType.ReadOnly<PrefabData>());

            NativeArray<Entity> views = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < views.Length; i++)
                {
                    if (m_PrefabSystem.TryGetPrefab(views[i], out InfoviewPrefab prefab)
                        && prefab != null
                        && prefab.name == kTemplateName)
                    {
                        return prefab;
                    }
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not search for the info view template: {e.Message}");
            }
            finally
            {
                views.Dispose();
            }

            return null;
        }
    }
}
