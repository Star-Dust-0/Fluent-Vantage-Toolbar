namespace FluentLegionToolbar;

static class L
{
    public static bool Ar;
    public static string Code = "en";
    public static void Set(string code) { Code = Extra.Table.ContainsKey(code) || code == "ar" ? code : "en"; Ar = Code == "ar"; }
    public static string T(string key)
    {
        if (!Table.TryGetValue(key, out var v)) return key;
        if (Ar) return v.ar;
        if (Code != "en" && Extra.Table.TryGetValue(Code, out var d) && d.TryGetValue(key, out var s)) return s;
        return v.en;
    }
    public static string T(string en, string ar) => Ar ? ar : en;

    static readonly Dictionary<string, (string en, string ar)> Table = new()
    {
        ["title"] = ("My {device}", "جهازي {device}"),
        ["battery.header"] = ("MY BATTERY", "البطارية"),
        ["tile.thermal"] = ("Thermal mode", "الوضع الحراري"),
        ["quick.header"] = ("QUICK SETTINGS", "الإعدادات السريعة"),
        ["battery.link"] = ("Battery details", "تفاصيل البطارية"),
        ["all.settings"] = ("All settings", "جميع الإعدادات"),
        ["warranty.link"] = ("Warranty options", "خيارات الضمان"),
        ["tile.fn"] = ("Fn Lock", "قفل Fn"),
        ["tile.mic"] = ("Mute mic", "كتم الميكروفون"),
        ["tile.conserve"] = ("Conserve mode", "وضع الحفاظ"),
        ["tile.rapid"] = ("Rapid charging", "شحن سريع"),
        ["tile.touchpad"] = ("Touchpad", "قفل اللمس"),
        ["tile.refresh"] = ("Refresh rate", "معدل التحديث"),
        ["tile.usb"] = ("Always-on USB", "طاقة USB الدائمة"),
        ["menu.open"] = ("Open", "فتح"),
        ["menu.settings"] = ("Toolbar settings", "إعدادات الشريط"),
        ["menu.about"] = ("About", "حول"),
        ["back"] = ("Back", "رجوع"),
        ["settings.title"] = ("Toolbar settings", "إعدادات الشريط"),
        ["settings.gear"] = ("Toolbar settings", "إعدادات الشريط"),
        ["settings.language"] = ("Language", "لغة البرنامج"),
        ["settings.theme"] = ("Theme", "سمة البرنامج"),
        ["theme.system"] = ("Use system setting", "حسب النظام"),
        ["theme.light"] = ("Light", "فاتحة"),
        ["theme.dark"] = ("Dark", "داكنة"),
        ["settings.buttons"] = ("Buttons shown on the main screen", "الأزرار الظاهرة في الواجهة الرئيسية"),
        ["settings.links"] = ("Links shown on the main screen", "الروابط الظاهرة في الواجهة الرئيسية"),
        ["settings.exit"] = ("Exit the app", "إنهاء البرنامج"),
        ["on"] = ("", ""),
        ["about.title"] = ("About", "حول"),
        ["about.version"] = ("Version", "الإصدار"),
        ["about.developer"] = ("Developer: Mohamed Elnaggar", "المطوّر: محمد النجار"),
        ["about.contrib"] = ("Contributors: Mohamed Elnaggar (developer), app.instinct (development contributor)", "المساهمون: محمد النجار (المطوّر)، app.instinct (مساهم في التطوير)"),
        ["about.credit"] = ("brought to you by Instinct", "مقدّم إليكم من Instinct"),
        ["about.note"] = ("Hardware protocols were studied from LenovoLegionToolkit (GPL-3.0). This app is an independent implementation and does not stop Lenovo services.", "دُرست بروتوكولات العتاد من مشروع LenovoLegionToolkit ‏(GPL-3.0). هذا البرنامج تنفيذ مستقل ولا يوقف خدمات Lenovo."),
        ["bat.title"] = ("Battery details", "تفاصيل البطارية"),
        ["bat.header"] = ("Battery", "البطارية"),
        ["bat.charge"] = ("Current charge:", "الشحن الحالي:"),
        ["bat.health"] = ("Battery health:", "صحة البطارية:"),
        ["bat.design"] = ("Design capacity:", "السعة التصميمية:"),
        ["bat.full"] = ("Full charge capacity:", "سعة الشحن الكامل:"),
        ["bat.cycles"] = ("Cycle count:", "عدد دورات الشحن:"),
        ["bat.date"] = ("Manufacture date:", "تاريخ التصنيع:"),
        ["bat.unavailable"] = ("Unavailable", "غير متاح"),
        ["war.title"] = ("Warranty", "الضمان"),
        ["war.expired"] = ("Expired", "منتهي"),
        ["war.active"] = ("Active", "ساري"),
        ["war.start"] = ("Start date:", "تاريخ البدء:"),
        ["war.end"] = ("End date:", "تاريخ الانتهاء:"),
        ["war.days"] = ("Days remaining:", "الأيام المتبقية:"),
        ["war.support"] = ("Lenovo Support", "دعم Lenovo"),
        ["war.loading"] = ("Reading warranty information from Lenovo...", "جارٍ قراءة بيانات الضمان من Lenovo..."),
        ["war.failed"] = ("Warranty information could not be read. Check the internet connection and try again.", "تعذرت قراءة بيانات الضمان. تحقق من الاتصال بالإنترنت ثم أعد المحاولة."),
        ["war.refresh"] = ("Refresh", "تحديث"),
        ["status.ok"] = ("Hardware states read successfully.", "قُرئت حالات العتاد بنجاح."),
        ["status.some"] = ("Some controls are unavailable. Open battery details for more information.", "بعض العناصر غير متاحة. افتح تفاصيل البطارية للمزيد."),
        ["charging"] = ("Charging", "جارٍ الشحن"),
        ["plugged"] = ("Plugged in, not charging", "متصل بالطاقة، لا يشحن"),
        ["onbattery"] = ("On battery", "يعمل بالبطارية"),
        ["preview"] = ("Visual preview only. No hardware commands are sent.", "معاينة بصرية فقط. لا تُرسل أوامر إلى الجهاز."),
        ["notconfirmed"] = ("Not confirmed: ", "لم يتم التأكيد: "),
        ["tip.fn"] = ("Fn Lock: when on, F1-F12 act as function keys.", "قفل Fn: عند تفعيله تعمل مفاتيح F1 إلى F12 كمفاتيح وظيفية."),
        ["tip.mic"] = ("Mute all active recording endpoints, including external microphones.", "كتم جميع أجهزة التسجيل النشطة، بما فيها الميكروفونات الخارجية."),
        ["tip.conserve"] = ("Battery conservation. Mutually exclusive with rapid charge.", "الحفاظ على البطارية. لا يعمل مع الشحن السريع في الوقت نفسه."),
        ["tip.rapid"] = ("Rapid charging. Mutually exclusive with conservation.", "الشحن السريع. لا يعمل مع وضع الحفاظ في الوقت نفسه."),
        ["tip.touchpad"] = ("Lock touchpad. Use Fn+F10 or an external mouse to recover.", "قفل لوحة اللمس. استخدم Fn+F10 أو فأرة خارجية للاستعادة."),
        ["tip.refresh.ok"] = ("Switch the internal panel between its lowest and highest supported refresh rates.", "تبديل الشاشة الداخلية بين أدنى وأعلى تردد مدعوم."),
        ["tip.refresh.no"] = ("Two supported refresh rates are required. No unsupported mode is applied.", "لا يتوفر ترددان مدعومان. لن يُفرض وضع شاشة غير مدعوم."),
        ["tip.usb"] = ("Always-on USB can drain the battery. BIOS and firmware policies still apply.", "قد تستنزف طاقة USB الدائمة البطارية. تظل سياسات BIOS والفيرموير سارية."),
        ["dlg.touchpad.title"] = ("Lock the touchpad?", "قفل لوحة اللمس؟"),
        ["dlg.touchpad.body"] = ("Make sure you have an external mouse or can use Fn+F10 to unlock it.", "تأكد من توفر فأرة خارجية أو إمكانية استخدام Fn+F10 لفتح القفل."),
        ["dlg.lock"] = ("Lock", "قفل"),
        ["dlg.cancel"] = ("Cancel", "إلغاء"),
        ["lang.name.ar"] = ("العربية", "العربية"),
        ["lang.name.en"] = ("English", "English"),
    };
}



