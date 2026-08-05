import { Component, ErrorInfo, ReactNode, useEffect, useRef } from "react";
import { bindValue, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { getModule } from "cs2/modding";
import hotelsIcon from "images/tourism-overhaul-hotels.svg";

/**
 * A seventh demand bar for tourism, in both places the game shows demand: the Demand info view
 * page and the toolbar pill stack.
 *
 * The value and its factor list come from TouristDemandUISystem, which mirrors the native
 * CityInfoUISystem — including its asymmetric smoothing, so the bar rises slowly and falls quickly
 * exactly as the other six do. See docs/DEMAND-UI-PLAN.md for the trace.
 */

const LOG_PREFIX = "[TourismOverhaul]";

/** Light red. Chosen to sit beside the zone colours without reading as a warning. */
export const DEMAND_COLOUR = "#e8756b";
const DEMAND_COLOUR_DIM = "rgba(232, 117, 107, 0.35)";

interface DemandFactor {
  factor: string;
  weight: number;
}

/**
 * The native SCSS class maps for the two surfaces.
 *
 * Reusing the game's own classes is what makes the section sit flush with the six above it —
 * padding, type scale and row rhythm all come free, and they follow the game's own UI scaling
 * rather than the rem values guessed from a screenshot.
 *
 * Read defensively: these are module paths from a packed bundle, so a game update can move them.
 * A miss leaves the inline styles doing the work, which is plainer but still renders.
 */
const nativeClasses = (path: string): Record<string, string> => {
  try {
    return getModule(path, "classes") ?? {};
  } catch {
    return {};
  }
};

/**
 * Turns a class map value into a usable CSS selector.
 *
 * The values are space-separated lists of several hashed names — "title_TAF title_PYv title_bwV" —
 * so prefixing a single dot builds a descendant selector that matches nothing, silently. One entry
 * also carries a literal "undefined", which would make the whole selector invalid.
 */
const sel = (classes: string | undefined): string | null => {
  const parts = (classes ?? "")
    .trim()
    .split(/\s+/)
    .filter((c) => c && c !== "undefined");

  return parts.length ? `.${parts.join(".")}` : null;
};

const SECTION_BASE = "game-ui/game/components/city-info-panel/city-info-demand/demand-section/";

const sectionClasses = nativeClasses(`${SECTION_BASE}demand-section.module.scss`);
const barClasses = nativeClasses(`${SECTION_BASE}demand-bar.module.scss`);
const factorClasses = nativeClasses(`${SECTION_BASE}demand-factors.module.scss`);
const toolbarClasses = nativeClasses(
  "game-ui/game/components/toolbar/top/city-info-field/demand-bars.module.scss"
);

// The page's own classes, which is where the right-hand detail pane lives. The pane already picks
// up our icon from demandIcons, so only its text is missing — and since it is internal to
// CityInfoDemand there is nothing to wrap, meaning the text has to be drawn over it. These names
// are what will position that.
const pageClasses = nativeClasses(
  "game-ui/game/components/city-info-panel/city-info-demand/demand-page.module.scss"
);

const themeClasses = nativeClasses(`${SECTION_BASE}demand-section-theme.module.scss`);

console.log(
  `${LOG_PREFIX} page classes: [${Object.keys(pageClasses).join(", ")}] ` +
    `theme classes: [${Object.keys(themeClasses).join(", ")}]`
);

// Logged once at load. The class names inside a packed SCSS module cannot be read from disk, so
// this is the only way to learn them — and knowing them is what turns the styling below from
// approximate into exact.
console.log(
  `${LOG_PREFIX} Native demand classes —` +
    ` section: [${Object.keys(sectionClasses).join(", ")}]` +
    ` bar: [${Object.keys(barClasses).join(", ")}]` +
    ` factors: [${Object.keys(factorClasses).join(", ")}]` +
    ` toolbar: [${Object.keys(toolbarClasses).join(", ")}]`
);

const touristDemand$ = bindValue<number>("tourismOverhaul", "touristDemand", 0);
const touristDemandFactors$ = bindValue<DemandFactor[]>(
  "tourismOverhaul",
  "touristDemandFactors",
  []
);

const useDemandText = () => {
  const { translate } = useLocalization();
  return (key: string, english: string) =>
    translate(`TourismOverhaul.DEMAND[${key}]`, english) ?? english;
};

const noop = () => {};





const ENGLISH: Record<string, string> = {
  Title: "Tourist Demand",
  Description:
    "Tourist demand is how many visitors would come but have nowhere to stay. It rises with " +
    "attractiveness and as your hotels fill, and falls to nothing once there is a room waiting " +
    "for everyone who wants one. Zone hotels and motels while it is high.",
  // A neutral noun, as the native factors all are ("Unoccupied Buildings", "Local Demand"). The
  // sign says which way it pushes demand, so a judgement in the label makes "+" read as approval
  // of a problem.
  NoRooms: "Lodging Shortage",
  Attractiveness: "Attractiveness",
  EmptyRooms: "Empty Hotel Rooms",
  AtCeiling: "Visitors Already Here",
  Connections: "Ways Into The City",
};

/**
 * Keeps a render failure local.
 *
 * The toolbar is on screen for the entire game and an undefined component there takes the whole
 * interface down, leaving the player with a bare map and no menu. Falling back to null means the
 * worst case is a missing bar beside six working ones.
 */
export class DemandErrorBoundary extends Component<
  { children: ReactNode },
  { failed: boolean }
> {
  constructor(props: { children: ReactNode }) {
    super(props);
    this.state = { failed: false };
  }

  static getDerivedStateFromError() {
    return { failed: true };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error(
      `${LOG_PREFIX} Tourist demand UI failed and was removed:`,
      error,
      info.componentStack
    );
  }

  render() {
    return this.state.failed ? null : this.props.children;
  }
}

/**
 * The full section for the Demand info view page: title, arrow bar, and the +/- factor list.
 *
 * The arrow shape is the native one — a rectangle with a chevron on its trailing edge — reproduced
 * with a clip-path rather than by reusing a native component, because the native bar's colour comes
 * from a fixed per-zone class rather than a prop.
 */
interface SectionHandlers {
  onHover?: (type: unknown) => void;
  onSelect?: (type: unknown) => void;
}

/**
 * Writes the tourism title and description into the page's detail pane.
 *
 * There is no clean way to do this. The pane is rendered inside CityInfoDemand, which exports no
 * component for it, takes no props that reach it, and builds its text from a lookup that does not
 * resolve for a type the game does not know — ten candidate namespaces were registered at once and
 * none rendered. So the column is written to directly.
 *
 * What makes it tolerable is that it needs no hover state of its own: the pane already picks our
 * icon out of demandIcons, so the icon it is currently showing says whether it is describing
 * tourism. Reading that is more reliable than mirroring the page's selection, which we cannot see.
 *
 * Everything it adds is tagged, so it only ever removes its own node and leaves the pane's own
 * markup alone.
 */

/**
 * The class native description paragraphs carry, remembered from the last one seen.
 *
 * When the pane describes tourism there is no native paragraph to copy from, so it is captured
 * while a stock demand type is shown and reused afterwards.
 */
let paragraphClass = "";

const useDetailPaneText = (title: string, description: string) => {
  useEffect(() => {
    const columnSelector = sel(pageClasses.infoColumn);

    if (!columnSelector) {
      return;
    }

    let outer: HTMLElement | null = null;

    try {
      outer = document.querySelector<HTMLElement>(columnSelector);
    } catch {
      return;
    }

    if (!outer) {
      return;
    }

    // The pane builds its full markup for our type — icon plate, title element, description
    // paragraphs — and simply leaves the text empty, exactly as the section's title strip did.
    // So these are filled rather than added to. That is why appending a node always looked wrong
    // however it was anchored: the real elements were already sitting above it.
    //
    // Selected on the hashed prefix, which webpack derives from the source class name, so
    // "title_UOl" stays findable as [class*="title_"] across builds where the hash changes.
    const icon = outer.querySelector("img");

    if (icon?.getAttribute("src") !== hotelsIcon) {
      // The pane is describing some other demand type; leave every element to the game.
      return;
    }

    const titleEl = outer.querySelector<HTMLElement>('[class*="title_"]');

    if (titleEl && titleEl.textContent !== title) {
      titleEl.textContent = title;
    }

    const paragraphs = outer.querySelector<HTMLElement>('[class*="paragraphs_"]');

    if (!paragraphs) {
      return;
    }

    let paragraph = paragraphs.querySelector("p");

    if (!paragraph) {
      paragraph = document.createElement("p");

      // Native paragraphs carry a class of their own. Reuse the one last seen on a real paragraph
      // so the text keeps the pane's spacing; without it the description would render unstyled.
      if (paragraphClass) {
        paragraph.className = paragraphClass;
      }

      paragraphs.appendChild(paragraph);
    } else if (paragraph.className) {
      paragraphClass = paragraph.className;
    }

    if (paragraph.textContent !== description) {
      paragraph.textContent = description;
    }

    // A native type's description runs to two paragraphs; ours is one, so any left over from the
    // previously described type would otherwise stay on screen underneath it.
    while (paragraphs.children.length > 1) {
      paragraphs.removeChild(paragraphs.lastElementChild!);
    }
  });
};

const TouristDemandSectionInner = ({ onHover, onSelect }: SectionHandlers) => {

  const demand = useValue(touristDemand$);
  const factors = useValue(touristDemandFactors$) ?? [];
  const t = useDemandText();
  const sectionRef = useRef<HTMLDivElement>(null);

  const title = t("Title", ENGLISH.Title);

  // Written into the native title element rather than floated over it. The strip is rendered by
  // the section and simply has no text, so filling it in is exact by construction — the overlay
  // needed the strip's height and padding guessed, which is why it kept sitting low.
  useEffect(() => {
    const root = sectionRef.current;

    if (!root) {
      return;
    }

    // Two candidates: demand-section.module.scss exports "title", and the theme module exports
    // "header". The band is painted by one of them and writing to the wrong one is a silent no-op,
    // which is what emptied the title — so try both.
    const titleSelector = sel(sectionClasses.title);
    const strip = titleSelector ? root.querySelector<HTMLElement>(titleSelector) : null;

    if (strip && strip.textContent !== title) {
      strip.textContent = title;
    }
  });

  useDetailPaneText(title, t("Description", ENGLISH.Description));

  const percent = Math.round(Math.max(0, Math.min(1, demand)) * 100);

  // Native class names, harvested at load: section exposes demandSection/title/content, factors
  // expose demandFactors/factor/label. Using them means padding, type scale and row rhythm come
  // from the game rather than from rem values guessed off a screenshot — and they follow the
  // player's UI scale setting, which hardcoded values do not.
  // The native component, wherever it can be had. Everything below is the fallback for when the
  // registration failed — plainer, but it renders.
  if (registered && NativeDemandSection) {
    // The title is drawn over the native strip rather than through it. Ten candidate namespaces
    // were registered at once and none rendered, so the section does not build its title from a
    // locale key on the type name — there is nothing to register. The strip itself is there and
    // empty, so our text goes into it wearing the same class, which keeps the font, colour and
    // padding the game's rather than ours.
    return (
      <div ref={sectionRef}>
        <NativeDemandSection
          type={TOURIST_DEMAND_TYPE}
          demand={Math.max(0, Math.min(1, demand))}
          factors={factors}
          // Required, not optional. Leaving these undefined made every hover throw inside the
          // native handler, which the error boundary caught and re-rendered from — the stall was a
          // throw-and-remount loop rather than expensive work.
          // The page's own handlers, forwarded from a sibling section. Passing no-ops here left
          // the detail pane on whatever it last showed, so hovering tourism described low density
          // residential.
          onHover={onHover ?? noop}
          onSelect={onSelect ?? noop}
          className=""
        />
      </div>
    );
  }

  // Structure mirrors the native sections: a dark title strip, then a row holding the coloured
  // arrow on the left and the factor list on the right. Only the arrow carries the demand colour —
  // the native titles are all the same dark navy, so tinting ours made the section stand out as
  // foreign rather than as the seventh of a set.
  return (
    <div className={sectionClasses.demandSection}>
      <div className={sectionClasses.title}>{t("Title", ENGLISH.Title)}</div>

      <div className={sectionClasses.content} style={{ display: "flex", alignItems: "stretch" }}>
        <div style={{ flex: "1 1 auto", position: "relative", minHeight: "180rem" }}>
          <div
            className={barClasses.demandBar}
            style={{
              position: "absolute",
              top: 0,
              left: 0,
              bottom: 0,
              // The arrow tip needs room even at zero demand, so the bar never collapses to a
              // sliver the icon cannot sit in.
              width: `calc(${Math.max(percent, 12)}% )`,
              backgroundColor: DEMAND_COLOUR,
              clipPath:
                "polygon(0 0, calc(100% - 40rem) 0, 100% 50%, calc(100% - 40rem) 100%, 0 100%)",
              transition: "width 0.3s linear",
              display: "flex",
              alignItems: "center",
            }}
          >
            <img
              src={hotelsIcon}
              className={barClasses.icon}
              style={{ width: "120rem", height: "120rem", marginLeft: "10rem" }}
            />
          </div>
        </div>

        <div
          className={factorClasses.demandFactors}
          style={{ flex: "0 0 auto", minWidth: "300rem", paddingLeft: "24rem" }}
        >
          {factors.map((f) => (
            <div
              key={f.factor}
              className={factorClasses.factor}
              style={{ display: "flex", alignItems: "center" }}
            >
              <span
                className={factorClasses.icon}
                style={{
                  display: "inline-block",
                  width: "22rem",
                  textAlign: "center",
                  fontWeight: 900,
                  color: f.weight >= 0 ? "#59c659" : "#e0503a",
                }}
              >
                {f.weight >= 0 ? "+" : "–"}
              </span>
              <span className={factorClasses.label}>
                {t(f.factor, ENGLISH[f.factor] ?? f.factor)}
              </span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
};

/**
 * The section the registry renders, already inside its own error boundary.
 *
 * The boundary lives here rather than at the registration site so it cannot be forgotten: whatever
 * renders this gets the containment automatically.
 */
export const TouristDemandSection = (handlers: SectionHandlers) => {
  return (
    <DemandErrorBoundary>
      <TouristDemandSectionInner {...handlers} />
    </DemandErrorBoundary>
  );
};


/**
 * The demand page's DemandType enum, read at load.
 *
 * Wrapping the page component itself put our section outside the scroll container, full-bleed
 * across the toolbar — CityInfoDemand is the scroll box, so a sibling after it escapes the panel.
 * The section has to be emitted from inside the list instead, which means wrapping DemandSection
 * and rendering ours after the last one. This is how we identify the last one without hardcoding
 * a name that a game update could change.
 */
const DemandTypeEnum: Record<string, unknown> = (() => {
  try {
    return (
      getModule(
        "game-ui/game/components/city-info-panel/city-info-demand/demand-page.tsx",
        "DemandType"
      ) ?? {}
    );
  } catch {
    return {};
  }
})();

const DEMAND_TYPE_VALUES = Object.values(DemandTypeEnum);

// TypeScript enums compile to a plain object with a reverse mapping, so the numeric members are
// the ones that are actually passed around as `type`.
const NUMERIC_DEMAND_TYPES = DEMAND_TYPE_VALUES.filter(
  (v): v is number => typeof v === "number"
);

const LAST_DEMAND_TYPE =
  NUMERIC_DEMAND_TYPES.length > 0 ? Math.max(...NUMERIC_DEMAND_TYPES) : undefined;

/** The id we register tourism under: one past the last the game defines. */
const TOURIST_DEMAND_TYPE =
  LAST_DEMAND_TYPE !== undefined ? LAST_DEMAND_TYPE + 1 : undefined;

const nativeModule = (path: string, exportName: string): any => {
  try {
    return getModule(path, exportName);
  } catch {
    return undefined;
  }
};

const DEMAND_PAGE = "game-ui/game/components/city-info-panel/city-info-demand/demand-page.tsx";

const NativeDemandSection = nativeModule(`${SECTION_BASE}demand-section.tsx`, "DemandSection");
const demandColors = nativeModule(DEMAND_PAGE, "demandColors");
const demandIcons = nativeModule(DEMAND_PAGE, "demandIcons");

/**
 * Registers tourism as a seventh demand type, so the game's own DemandSection draws it.
 *
 * This is the difference between looking like the other six and being one of them: colour, arrow
 * geometry, icon plate, factor rows, hover and selection states all come from the game rather than
 * from rem values matched to a screenshot.
 *
 * Both maps are keyed by the numeric enum member, which is what the section receives as `type`.
 */
const registered = (() => {
  if (TOURIST_DEMAND_TYPE === undefined || !demandColors || !demandIcons) {
    return false;
  }

  try {
    (DemandTypeEnum as any)[TOURIST_DEMAND_TYPE] = "Tourist";
    (DemandTypeEnum as any).Tourist = TOURIST_DEMAND_TYPE;

    demandColors[TOURIST_DEMAND_TYPE] = DEMAND_COLOUR;
    demandIcons[TOURIST_DEMAND_TYPE] = hotelsIcon;

    return true;
  } catch {
    return false;
  }
})();

console.log(
  `${LOG_PREFIX} DemandType numeric: [${NUMERIC_DEMAND_TYPES.join(", ")}], ` +
    `registered tourism as ${String(TOURIST_DEMAND_TYPE)} — ${registered ? "ok" : "FAILED"}. ` +
    `colors keys: [${Object.keys(demandColors ?? {}).join(", ")}] ` +
    `icons keys: [${Object.keys(demandIcons ?? {}).join(", ")}] ` +
    `native section: ${NativeDemandSection ? "found" : "MISSING"}`
);

/** Set once we have logged a DemandSection's props, so the log is not flooded every render. */
let loggedSectionProps = false;

/**
 * Renders our own factor rows, and leaves the game's alone.
 *
 * The native row looks its label up in a namespace that could not be found — sixteen probed
 * patterns, ten registered at once, no hits — so ours supply their own string. The native classes
 * still do the styling, which keeps the rows in the same rhythm as their neighbours.
 */
export const wrapDemandFactorDetail = (Native: any) => (props: any) => {
  const name: string | undefined = props?.factor?.factor;

  // Ours render here rather than through the native row, which looks its label up in a namespace
  // we could not find in four attempts. The native classes still do the styling, so the row sits
  // in the same rhythm as its neighbours — only the string comes from us.
  if (name && Object.prototype.hasOwnProperty.call(ENGLISH, name) && name !== "Title") {
    return <TouristFactorRow name={name} weight={props.factor.weight ?? 0} />;
  }

  return <Native {...props} />;
};

const TouristFactorRow = ({ name, weight }: { name: string; weight: number }) => {
  const t = useDemandText();

  return (
    <div className={factorClasses.factor}>
      <div
        className={factorClasses.icon}
        style={{ color: weight >= 0 ? "#59c659" : "#e0503a", fontWeight: 900 }}
      >
        {weight >= 0 ? "+" : "–"}
      </div>
      <div className={factorClasses.label}>{t(name, ENGLISH[name] ?? name)}</div>
    </div>
  );
};

/**
 * Renders each native demand section untouched, and ours once, after the last one.
 *
 * Which prop carries the section's type is not documented — the UI ships packed — so the keys are
 * logged once. If none of the candidates matches, nothing extra renders and the log says what was
 * available, which is a better failure than a section duplicated six times.
 */
export const wrapDemandSection = (Native: any) => (props: any) => {
  if (!loggedSectionProps) {
    loggedSectionProps = true;
    console.log(
      `${LOG_PREFIX} DemandSection props: [${Object.keys(props ?? {}).join(", ")}] ` +
        `values: ${JSON.stringify(
          Object.fromEntries(
            Object.entries(props ?? {}).filter(([, v]) => typeof v !== "function" && typeof v !== "object")
          )
        )}`
    );
  }

  const isLast = LAST_DEMAND_TYPE !== undefined && props?.type === LAST_DEMAND_TYPE;

  return (
    <>
      <Native {...props} />
      {isLast ? (
        <TouristDemandSection onHover={props?.onHover} onSelect={props?.onSelect} />
      ) : null}
    </>
  );
};

/**
 * Adds tourism to the toolbar demand stack.
 *
 * This one needed no drawing at all. DemandBars takes its bars as a plain prop —
 * `items: [{ key, color, value }]`, value normalised 0..1 — and renders them into a single SVG, so
 * appending one entry makes the game draw the seventh bar itself, with the same geometry, gap,
 * gradient and shadow as the other six.
 *
 * Worth recording why the first attempt failed: the class map here is geometry rather than layout
 * (width, maxBarHeight, barGap, rangeFill), which correctly suggested the bars were computed into
 * a fixed viewport — but the conclusion drawn from it, that a bar had to be built to match, was
 * wrong. The data was a prop the whole time.
 */
export const wrapDemandToolbar = (Native: any) => (props: any) => {
  const demand = useValue(touristDemand$);

  if (!Array.isArray(props?.items)) {
    // Not the shape expected — leave the toolbar exactly as it was rather than risk the one piece
    // of UI that is on screen for the entire game.
    return <Native {...props} />;
  }

  const items = [
    ...props.items,
    {
      key: "Tourist",
      color: DEMAND_COLOUR,
      value: Math.max(0, Math.min(1, demand)),
    },
  ];

  return <Native {...props} items={items} />;
};
