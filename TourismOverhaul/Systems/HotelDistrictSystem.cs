// Replaced by HotelZoneSystem.
//
// This claimed vacant commercial premises inside districts marked as hotel districts and handed
// them to lodging companies. It worked, but only ever redecorated buildings that already existed —
// it could not make hotels get built, which is what was actually wanted.
//
// The reason it was built this way was a mistaken belief that the game had no hotel-specific
// building assets, so no zone could be filtered down to them. A runtime survey of the shipped
// prefabs disproved that: there are 80 lodging-only zoned buildings (EU/NA CommercialHotel01 and
// CommercialMotel01, every level and lot size), each carrying a BuildingProperties override that
// sets m_AllowedSold to Lodging alone. They were simply mixed in with ordinary shops across zone
// types 4, 7, 35 and 36.
//
// HotelZoneSystem creates dedicated hotel and motel zones and repoints those prefabs at them,
// which is what the district approach was a poor substitute for.
//
// Left as a comment rather than deleted so the reasoning, and the mistake, stay on record.
