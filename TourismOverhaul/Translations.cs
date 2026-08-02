using System.Collections.Generic;

namespace TourismOverhaul
{
    /// <summary>
    /// Translated labels for the locales Cities: Skylines II ships with.
    ///
    /// Labels only. The settings descriptions are several paragraphs each and full of domain terms —
    /// surge pricing, outside connections, attractiveness — and a confidently wrong description is
    /// worse than an English one, because a player cannot tell it is wrong. LocaleOverlay fills
    /// anything absent here from LocaleEN, so a player sees their own language wherever the
    /// interface names something and English only where the mod explains itself at length.
    ///
    /// Adding to a locale is additive and safe: contribute more keys and they take effect
    /// immediately; miss one and it stays readable rather than blank. Contributions welcome for the
    /// long descriptions from native speakers who play the game in that language.
    /// </summary>
    public static class Translations
    {
        /// <summary>Locale identifiers the game ships with, in its own order.</summary>
        public static readonly string[] SupportedLocales =
        {
            "de-DE", "es-ES", "fr-FR", "it-IT", "ja-JP", "ko-KR",
            "pl-PL", "pt-BR", "ru-RU", "zh-HANS", "zh-HANT"
        };

        /// <summary>
        /// Builds the key/value pairs for a locale, or an empty set if it is not translated.
        ///
        /// Keys are produced from the settings object so they cannot drift from the property names.
        /// </summary>
        public static IReadOnlyDictionary<string, string> For(
            string locale, TourismOverhaulSetting setting)
        {
            if (!Labels.TryGetValue(locale, out string[] values))
            {
                return new Dictionary<string, string>();
            }

            Dictionary<string, string> entries = new Dictionary<string, string>();

            for (int i = 0; i < Keys.Length && i < values.Length; i++)
            {
                if (!string.IsNullOrEmpty(values[i]))
                {
                    entries[Key(Keys[i], setting)] = values[i];
                }
            }

            return entries;
        }

        private static string Key(string name, TourismOverhaulSetting setting)
        {
            switch (name)
            {
                case "$mod": return setting.GetSettingsLocaleID();
                case "$tab": return setting.GetOptionTabLocaleID(TourismOverhaulSetting.SectionMain);
                case "$viewName": return "Assets.NAME[TourismOverhaul Finance]";
                case "$viewInfo": return "Infoviews.INFOVIEW[TourismOverhaul Finance]";
                case "$hotels": return "Assets.NAME[TourismOverhaul Hotels]";
                case "$motels": return "Assets.NAME[TourismOverhaul Motels]";
                default:
                    if (name[0] == '#')
                    {
                        return setting.GetOptionGroupLocaleID(name.Substring(1));
                    }

                    // "@Road" is an info-panel row label; the frontend asks for it by this key.
                    return name[0] == '@'
                        ? "TourismOverhaul.PANEL[" + name.Substring(1) + "]"
                        : setting.GetOptionLabelLocaleID(name);
            }
        }

        /// <summary>
        /// The strings each locale supplies, in order. A blank entry falls back to English, so a
        /// partial translation is valid.
        /// </summary>
        private static readonly string[] Keys =
        {
            "$mod", "$tab", "$viewName", "$viewInfo", "$hotels", "$motels",
            "#" + TourismOverhaulSetting.GroupDemand,
            "#" + TourismOverhaulSetting.GroupRouting,
            "#" + TourismOverhaulSetting.GroupHotels,
            "#" + TourismOverhaulSetting.GroupStay,
            "#" + TourismOverhaulSetting.GroupOutbound,
            "#" + TourismOverhaulSetting.GroupDisplay,
            "#" + TourismOverhaulSetting.GroupReporting,
            nameof(TourismOverhaulSetting.TouristsPerThousandCitizens),
            nameof(TourismOverhaulSetting.MaximumTourists),
            nameof(TourismOverhaulSetting.MaxArrivalsPerUpdate),
            nameof(TourismOverhaulSetting.ArrivalWeightRoad),
            nameof(TourismOverhaulSetting.ArrivalWeightTrain),
            nameof(TourismOverhaulSetting.ArrivalWeightAir),
            nameof(TourismOverhaulSetting.ArrivalWeightShip),
            nameof(TourismOverhaulSetting.TouristCarChance),
            nameof(TourismOverhaulSetting.HotelRoomMultiplier),
            nameof(TourismOverhaulSetting.EnableHotelZones),
            nameof(TourismOverhaulSetting.EnableHotelWelcome),
            nameof(TourismOverhaulSetting.AverageStayDays),
            nameof(TourismOverhaulSetting.EnableResidentHolidays),
            nameof(TourismOverhaulSetting.HighlightTourists),
            nameof(TourismOverhaulSetting.DiagnosticLogging),
            nameof(TourismOverhaulSetting.TouristShoppingChance),
            nameof(TourismOverhaulSetting.HistoricBuildingAttractiveness),
            nameof(TourismOverhaulSetting.EnableAttractionCrowding),
            nameof(TourismOverhaulSetting.LodgingCostPercent),
            nameof(TourismOverhaulSetting.SpendingPerNightPercent),

            // Info-panel row labels, in the order the two panels draw them.
            "@TouristsInCity", "@HotelRoomsFree", "@Occupancy", "@LocalCimsAway",
            "@ArrivalsByMode", "@Road", "@Train", "@Plane", "@Sea",
            "@TouristSpending", "@Hotels", "@Shops", "@Fares", "@Leisure", "@Unattributed"
        };

        private static readonly Dictionary<string, string[]> Labels =
            new Dictionary<string, string[]>
        {
            ["de-DE"] = new[]
            {
                "Tourismus-Überarbeitung", "Allgemein", "Tourismusfinanzen", "Tourismusfinanzen",
                "Hotels", "Motels",
                "Touristennachfrage", "Ankünfte", "Hotels", "Aufenthaltsdauer",
                "Reisende Einwohner", "Anzeige", "Berichte",
                "Touristen pro 1000 Einwohner", "Maximale Touristenzahl", "Ankunftsgeschwindigkeit",
                "Ankünfte per Straße", "Ankünfte per Bahn", "Ankünfte per Flugzeug",
                "Ankünfte per Schiff", "Touristen mit Auto", "Hotelzimmer-Multiplikator",
                "Hotel- und Motelzonen", "Eröffnungsschub für neue Hotels",
                "Durchschnittliche Aufenthaltsdauer (Tage)", "Einwohner machen Urlaub",
                "Touristen markieren", "Diagnoseprotokoll",
                "Einkaufen statt Besichtigen", "Anziehungskraft historischer Gebäude",
                "Überfüllte Orte verlieren an Reiz", "Hotelzimmerpreis", "Taschengeld pro Nacht",
                "Touristen in der Stadt", "Freie Hotelzimmer", "Auslastung",
                "Einwohner unterwegs", "Ankünfte nach Verkehrsmittel",
                "Straße", "Bahn", "Flugzeug", "Schiff",
                "Touristenausgaben", "Hotels", "Geschäfte", "Fahrkarten", "Freizeit",
                "Nicht zugeordnet"
            },
            ["es-ES"] = new[]
            {
                "Revisión del turismo", "General", "Finanzas turísticas", "Finanzas turísticas",
                "Hoteles", "Moteles",
                "Demanda turística", "Llegadas", "Hoteles", "Duración de la estancia",
                "Residentes de viaje", "Visualización", "Informes",
                "Turistas por cada 1000 habitantes", "Turistas máximos", "Velocidad de llegada",
                "Llegadas por carretera", "Llegadas por tren", "Llegadas por avión",
                "Llegadas por mar", "Turistas que llegan en coche", "Multiplicador de habitaciones",
                "Zonas de hoteles y moteles", "Impulso de apertura para hoteles nuevos",
                "Estancia media (días)", "Los residentes se van de vacaciones",
                "Marcar turistas", "Registro de diagnóstico",
                "Compras antes que turismo", "Atractivo de edificios históricos",
                "Los lugares llenos pierden atractivo", "Precio de la habitación",
                "Dinero para gastar por noche",
                "Turistas en la ciudad", "Habitaciones libres", "Ocupación",
                "Residentes fuera", "Llegadas por medio",
                "Carretera", "Tren", "Avión", "Mar",
                "Gasto turístico", "Hoteles", "Tiendas", "Billetes", "Ocio",
                "Sin asignar"
            },
            ["fr-FR"] = new[]
            {
                "Refonte du tourisme", "Général", "Finances du tourisme", "Finances du tourisme",
                "Hôtels", "Motels",
                "Demande touristique", "Arrivées", "Hôtels", "Durée du séjour",
                "Résidents en voyage", "Affichage", "Rapports",
                "Touristes pour 1000 habitants", "Touristes maximum", "Vitesse d'arrivée",
                "Arrivées par la route", "Arrivées par le rail", "Arrivées par avion",
                "Arrivées par la mer", "Touristes arrivant en voiture",
                "Multiplicateur de chambres", "Zones d'hôtels et de motels",
                "Coup de pouce à l'ouverture", "Durée moyenne du séjour (jours)",
                "Les résidents partent en vacances", "Marquer les touristes",
                "Journal de diagnostic", "Achats plutôt que visites",
                "Attrait des bâtiments historiques", "Les lieux bondés perdent leur attrait",
                "Prix de la chambre", "Argent de poche par nuit",
                "Touristes en ville", "Chambres libres", "Taux d'occupation",
                "Résidents absents", "Arrivées par mode",
                "Route", "Train", "Avion", "Mer",
                "Dépenses des touristes", "Hôtels", "Commerces", "Billets", "Loisirs",
                "Non attribué"
            },
            ["it-IT"] = new[]
            {
                "Revisione del turismo", "Generale", "Finanze turistiche", "Finanze turistiche",
                "Hotel", "Motel",
                "Domanda turistica", "Arrivi", "Hotel", "Durata del soggiorno",
                "Residenti in viaggio", "Visualizzazione", "Rapporti",
                "Turisti ogni 1000 abitanti", "Turisti massimi", "Velocità di arrivo",
                "Arrivi su strada", "Arrivi in treno", "Arrivi in aereo", "Arrivi via mare",
                "Turisti che arrivano in auto", "Moltiplicatore camere",
                "Zone per hotel e motel", "Spinta di apertura per i nuovi hotel",
                "Soggiorno medio (giorni)", "I residenti vanno in vacanza",
                "Evidenzia i turisti", "Registro diagnostico",
                "Shopping invece di visite", "Attrattiva degli edifici storici",
                "I luoghi affollati perdono attrattiva", "Prezzo della camera",
                "Denaro da spendere a notte",
                "Turisti in città", "Camere libere", "Occupazione",
                "Residenti fuori città", "Arrivi per mezzo",
                "Strada", "Treno", "Aereo", "Mare",
                "Spesa turistica", "Hotel", "Negozi", "Biglietti", "Svago",
                "Non attribuito"
            },
            ["ja-JP"] = new[]
            {
                "観光オーバーホール", "全般", "観光収支", "観光収支", "ホテル", "モーテル",
                "観光需要", "到着", "ホテル", "滞在期間", "住民の旅行", "表示", "レポート",
                "住民1000人あたりの観光客", "観光客の上限", "到着速度",
                "道路からの到着", "鉄道からの到着", "空路からの到着", "海路からの到着",
                "車で来る観光客", "客室数の倍率", "ホテル・モーテル地区",
                "新規ホテルの開業ブースト", "平均滞在日数", "住民が休暇に出かける",
                "観光客を強調表示", "診断ログ", "観光より買い物", "歴史的建造物の魅力",
                "混雑した場所は魅力が下がる", "客室料金", "1泊あたりの小遣い",
                "市内の観光客", "空室数", "稼働率", "外出中の住民", "交通手段別の到着",
                "道路", "鉄道", "航空", "海路",
                "観光客の支出", "ホテル", "店舗", "運賃", "レジャー", "未分類"
            },
            ["ko-KR"] = new[]
            {
                "관광 개편", "일반", "관광 재정", "관광 재정", "호텔", "모텔",
                "관광 수요", "도착", "호텔", "체류 기간", "주민 여행", "표시", "보고",
                "주민 1000명당 관광객", "최대 관광객 수", "도착 속도",
                "도로 도착", "철도 도착", "항공 도착", "해상 도착",
                "차량으로 오는 관광객", "객실 수 배율", "호텔 및 모텔 구역",
                "신규 호텔 개장 부스트", "평균 체류 일수", "주민이 휴가를 떠남",
                "관광객 강조 표시", "진단 로그", "관광보다 쇼핑", "역사적 건물의 매력",
                "혼잡한 장소는 매력이 감소", "객실 요금", "1박당 지출 금액",
                "도시 내 관광객", "빈 객실", "객실 점유율", "외지에 나간 주민", "교통수단별 도착",
                "도로", "철도", "항공", "해상",
                "관광객 지출", "호텔", "상점", "요금", "여가", "미분류"
            },
            ["pl-PL"] = new[]
            {
                "Przebudowa turystyki", "Ogólne", "Finanse turystyki", "Finanse turystyki",
                "Hotele", "Motele",
                "Popyt turystyczny", "Przyjazdy", "Hotele", "Długość pobytu",
                "Podróże mieszkańców", "Wyświetlanie", "Raporty",
                "Turyści na 1000 mieszkańców", "Maksymalna liczba turystów",
                "Szybkość przyjazdów", "Przyjazdy drogą", "Przyjazdy koleją",
                "Przyjazdy samolotem", "Przyjazdy drogą morską", "Turyści przyjeżdżający autem",
                "Mnożnik pokoi hotelowych", "Strefy hoteli i moteli",
                "Wsparcie na otwarcie hotelu", "Średnia długość pobytu (dni)",
                "Mieszkańcy wyjeżdżają na wakacje", "Wyróżnij turystów", "Dziennik diagnostyczny",
                "Zakupy zamiast zwiedzania", "Atrakcyjność budynków historycznych",
                "Zatłoczone miejsca tracą atrakcyjność", "Cena pokoju",
                "Kieszonkowe na noc",
                "Turyści w mieście", "Wolne pokoje", "Obłożenie",
                "Mieszkańcy poza miastem", "Przyjazdy według środka transportu",
                "Droga", "Kolej", "Samolot", "Morze",
                "Wydatki turystów", "Hotele", "Sklepy", "Bilety", "Rozrywka",
                "Nieprzypisane"
            },
            ["pt-BR"] = new[]
            {
                "Reformulação do turismo", "Geral", "Finanças do turismo", "Finanças do turismo",
                "Hotéis", "Motéis",
                "Demanda turística", "Chegadas", "Hotéis", "Duração da estadia",
                "Moradores viajando", "Exibição", "Relatórios",
                "Turistas por 1000 habitantes", "Máximo de turistas", "Velocidade de chegada",
                "Chegadas por estrada", "Chegadas por trem", "Chegadas por avião",
                "Chegadas por mar", "Turistas chegando de carro", "Multiplicador de quartos",
                "Zonas de hotéis e motéis", "Impulso de inauguração para novos hotéis",
                "Estadia média (dias)", "Moradores saem de férias",
                "Destacar turistas", "Registro de diagnóstico",
                "Compras em vez de passeios", "Atratividade de prédios históricos",
                "Lugares lotados perdem o apelo", "Preço do quarto",
                "Dinheiro para gastar por noite",
                "Turistas na cidade", "Quartos livres", "Ocupação",
                "Moradores fora da cidade", "Chegadas por meio de transporte",
                "Rodovia", "Trem", "Avião", "Mar",
                "Gastos dos turistas", "Hotéis", "Lojas", "Passagens", "Lazer",
                "Não atribuído"
            },
            ["ru-RU"] = new[]
            {
                "Переработка туризма", "Общие", "Финансы туризма", "Финансы туризма",
                "Отели", "Мотели",
                "Спрос на туризм", "Прибытия", "Отели", "Длительность пребывания",
                "Жители в поездках", "Отображение", "Отчёты",
                "Туристов на 1000 жителей", "Максимум туристов", "Скорость прибытия",
                "Прибытие по дороге", "Прибытие по железной дороге", "Прибытие по воздуху",
                "Прибытие по морю", "Туристы, приезжающие на машине",
                "Множитель номеров", "Зоны отелей и мотелей",
                "Бонус при открытии отеля", "Средняя длительность пребывания (дни)",
                "Жители уезжают в отпуск", "Выделять туристов", "Журнал диагностики",
                "Покупки вместо осмотра достопримечательностей",
                "Привлекательность исторических зданий",
                "Переполненные места теряют привлекательность", "Цена номера",
                "Карманные деньги за ночь",
                "Туристов в городе", "Свободных номеров", "Заполняемость",
                "Жителей в отъезде", "Прибытия по видам транспорта",
                "Дорога", "Железная дорога", "Самолёт", "Море",
                "Расходы туристов", "Отели", "Магазины", "Проезд", "Досуг",
                "Не распределено"
            },
            ["zh-HANS"] = new[]
            {
                "旅游系统改造", "常规", "旅游财务", "旅游财务", "酒店", "汽车旅馆",
                "旅游需求", "到达", "酒店", "停留时长", "居民出行", "显示", "报告",
                "每千名居民的游客数", "最大游客数", "到达速度",
                "公路到达", "铁路到达", "航空到达", "海运到达",
                "自驾到达的游客", "客房数量倍率", "酒店与汽车旅馆分区",
                "新酒店开业加成", "平均停留天数", "居民外出度假",
                "标记游客", "诊断日志", "购物优先于观光", "历史建筑吸引力",
                "拥挤地点吸引力下降", "客房价格", "每晚消费金额",
                "城中游客", "空余客房", "入住率", "外出的居民", "各方式到达量",
                "公路", "铁路", "航空", "海运",
                "游客消费", "酒店", "商店", "票价", "休闲", "未分类"
            },
            ["zh-HANT"] = new[]
            {
                "旅遊系統改造", "一般", "旅遊財務", "旅遊財務", "飯店", "汽車旅館",
                "旅遊需求", "抵達", "飯店", "停留時間", "居民出遊", "顯示", "報告",
                "每千名居民的遊客數", "最大遊客數", "抵達速度",
                "公路抵達", "鐵路抵達", "航空抵達", "海運抵達",
                "自行開車抵達的遊客", "客房數量倍率", "飯店與汽車旅館分區",
                "新飯店開幕加成", "平均停留天數", "居民外出度假",
                "標記遊客", "診斷紀錄", "購物優先於觀光", "歷史建築吸引力",
                "擁擠地點吸引力下降", "客房價格", "每晚消費金額",
                "城中遊客", "空房數", "住房率", "外出的居民", "各方式抵達量",
                "公路", "鐵路", "航空", "海運",
                "遊客消費", "飯店", "商店", "票價", "休閒", "未分類"
            }
        };
    }
}
