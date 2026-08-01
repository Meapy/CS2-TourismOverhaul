// Superseded by HotelDistrictSection.
//
// This was a plain UISystemBase publishing "isDistrictSelected" / "isHotelDistrict" bindings for a
// React component that wrapped the district panel. That approach cannot work: the selected-info
// panel has no per-entity panel component to wrap. It looks sections up in a map keyed by the C#
// section system's full type name — see InfoSectionBase.Write, which emits
// writer.TypeBegin(GetType().FullName).
//
// HotelDistrictSection is the correct form: an InfoSectionBase that owns its own visibility and
// writes its properties, paired with a React component registered under
// "TourismOverhaul.Systems.HotelDistrictSection".
//
// Deleting this file is safe. It is left empty rather than removed so the old approach, and why it
// failed, stays on record — and so nothing re-registers a second "toggleHotelDistrict" trigger
// under the same binding name.
