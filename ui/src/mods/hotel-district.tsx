import { trigger } from "cs2/api";
import * as CS2UI from "cs2/ui";

/**
 * "Hotel district" row in the selected-info panel.
 *
 * Sections are not free-standing components. The frontend keeps a map,
 * selectedInfoSectionComponents, keyed by the C# section system's full type name — see
 * InfoSectionBase.Write, which emits writer.TypeBegin(GetType().FullName). The C# side decides
 * when the section is visible and hands its data over as props, so this component only renders.
 *
 * The key below must match the C# class exactly. Renaming either side silently drops the row.
 */
export const HOTEL_DISTRICT_SECTION_KEY = "TourismOverhaul.Systems.HotelDistrictSection";

const ui = CS2UI as any;

const FallbackRow = ({ left, right }: { left?: any; right?: any }) => (
  <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
    <div>{left}</div>
    <div>{right}</div>
  </div>
);

const Row: any = ui?.InfoRow ?? ui?.PanelSectionRow ?? FallbackRow;
const Section: any =
  ui?.InfoSection ?? ui?.PanelSection ?? (({ children }: any) => <div>{children}</div>);

// cs2/ui exports Button, MenuButton, FloatingButton and Icon — but no Checkbox or Toggle. An
// earlier version reached for ui.Checkbox, got undefined, and fell through to a bare unstyled
// <button>, which is why the row rendered as plain black text.
const Button: any = ui?.Button ?? (({ children, ...p }: any) => <button {...p}>{children}</button>);

/**
 * Props come from HotelDistrictSection.OnWriteProperties on the C# side. Visibility is decided
 * there too, so no selection check is needed here.
 */
export const HotelDistrictSectionComponent = ({
  isHotelDistrict,
}: {
  isHotelDistrict?: boolean;
}) => {
  const on = !!isHotelDistrict;
  const toggle = () => trigger("tourismOverhaul", "toggleHotelDistrict");

  return (
    <Section>
      <Row
        left="Hotel district"
        uppercase={false}
        tooltip={
          on
            ? "Vacant commercial premises here are offered to hotels first. The zoning stays " +
              "ordinary commercial, so removing this mod changes nothing."
            : "Turn on to have vacant commercial premises in this district offered to hotels " +
              "before other businesses."
        }
        right={
          // Every other row in this panel puts plain text on the right, so a text-variant button
          // sits in place rather than dropping a large coloured block into the layout.
          // "primary" renders a full-size call-to-action button, which is what made it dominate.
          //
          // onSelect only. Wiring onClick as well fires the trigger twice per click, which toggled
          // the district off and straight back on — visible in the log as a "no longer"/"marked"
          // pair a millisecond apart.
          <Button variant="text" onSelect={toggle}>
            {on ? "On" : "Off"}
          </Button>
        }
      />
    </Section>
  );
};

/**
 * Registers the component into the section map.
 *
 * The map is an object keyed by type name, not an array — an earlier attempt pushed to it as if it
 * were a list, which silently did nothing.
 */
export function registerHotelDistrictSection(components: any): any {
  if (components == null) {
    console.warn("[TourismOverhaul] selectedInfoSectionComponents was null; section not added.");
    return components;
  }

  components[HOTEL_DISTRICT_SECTION_KEY] = HotelDistrictSectionComponent;
  console.log(`[TourismOverhaul] Section registered as "${HOTEL_DISTRICT_SECTION_KEY}".`);

  return components;
}
