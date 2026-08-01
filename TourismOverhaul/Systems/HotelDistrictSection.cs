// Replaced by HotelZoneSystem.
//
// This was the "Hotel district" toggle in the selected-info panel. Hotel areas are now painted with
// a dedicated zone rather than marked on a district, so the toggle has nothing to control.
//
// Kept as a comment for the technique, which was hard-won and applies to any info panel row:
//
//   - Sections are not free-standing React components. The frontend holds a map,
//     selectedInfoSectionComponents, keyed by the C# section system's full type name — see
//     InfoSectionBase.Write, which emits writer.TypeBegin(GetType().FullName).
//   - Creating the section system is not enough. SelectedInfoUISystem.AddSections (:222-252) fills
//     three private List<ISectionSource> fields during its own OnCreate and exposes no way to add
//     to them, so a mod section has to be inserted by reflection or the panel never asks about it.
//   - Selection comes from SelectedInfoUISystem.selectedEntity, not ToolSystem.selected, which is
//     empty while merely browsing a panel.
//   - InfoSectionBase.PerformUpdate gates everything behind m_Dirty, including the Update() call
//     that runs OnUpdate, so a section must mark itself dirty in OnPreUpdate or it evaluates once
//     and freezes.
//   - Changing a value is not enough to refresh the panel: SelectedInfoUISystem.SetDirty() has to
//     be called, or the row keeps showing the old value until the selection changes.
