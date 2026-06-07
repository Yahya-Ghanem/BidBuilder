// 21.4 — Pure i18n core (no React) so the dictionaries + translator are unit-testable
// in the node vitest environment. The provider/hooks live in lib/i18n.tsx and re-export
// everything here.

export type Locale = "en" | "ar"
export const LOCALES: Locale[] = ["en", "ar"]
const RTL: Locale[] = ["ar"]

type Dict = Record<string, string>

const en: Dict = {
  "nav.projects": "Projects",
  "nav.resources": "Resource Library",
  "nav.quotes": "Quotes Register",
  "nav.subQuotes": "Subcontractor Quotes",
  "nav.assemblies": "Assemblies",
  "nav.benchmarks": "Benchmarks",
  "nav.analytics": "Bid Analytics",
  "nav.users": "Users & Teams",
  "nav.audit": "Audit log",
  "nav.settings": "Settings",
  "shell.account": "Account & security",
  "shell.signOut": "Sign out",
  "shell.loading": "Loading…",
  "shell.openMenu": "Open menu",
  "shell.closeMenu": "Close menu",
  "login.subtitle": "Sign in to your estimating workspace.",
  "login.tenant": "Company (tenant)",
  "login.email": "Email",
  "login.password": "Password",
  "login.signIn": "Sign in",
  "login.signingIn": "Signing in…",
  "login.or": "or",
  "login.sso": "Continue with SSO",
  "login.platformAdmin": "Platform administration",
  "lang.label": "Language",
  "lang.en": "English",
  "lang.ar": "العربية",
  "common.save": "Save",
  "common.cancel": "Cancel",
  "common.delete": "Delete",
  "common.add": "Add",
  "common.create": "Create",
  "common.edit": "Edit",
}

const ar: Dict = {
  "nav.projects": "المشاريع",
  "nav.resources": "مكتبة الموارد",
  "nav.quotes": "سجل عروض الأسعار",
  "nav.subQuotes": "عروض المقاولين من الباطن",
  "nav.assemblies": "التجميعات",
  "nav.benchmarks": "المقارنات المرجعية",
  "nav.analytics": "تحليلات العطاءات",
  "nav.users": "المستخدمون والفِرق",
  "nav.audit": "سجل التدقيق",
  "nav.settings": "الإعدادات",
  "shell.account": "الحساب والأمان",
  "shell.signOut": "تسجيل الخروج",
  "shell.loading": "جارٍ التحميل…",
  "shell.openMenu": "فتح القائمة",
  "shell.closeMenu": "إغلاق القائمة",
  "login.subtitle": "سجّل الدخول إلى مساحة عمل التقدير.",
  "login.tenant": "الشركة (المستأجر)",
  "login.email": "البريد الإلكتروني",
  "login.password": "كلمة المرور",
  "login.signIn": "تسجيل الدخول",
  "login.signingIn": "جارٍ تسجيل الدخول…",
  "login.or": "أو",
  "login.sso": "المتابعة عبر الدخول الموحّد",
  "login.platformAdmin": "إدارة المنصّة",
  "lang.label": "اللغة",
  "lang.en": "English",
  "lang.ar": "العربية",
  "common.save": "حفظ",
  "common.cancel": "إلغاء",
  "common.delete": "حذف",
  "common.add": "إضافة",
  "common.create": "إنشاء",
  "common.edit": "تعديل",
}

export const messages: Record<Locale, Dict> = { en, ar }

export function isRtl(locale: Locale): boolean {
  return RTL.includes(locale)
}

/** Pure translator: look up the key in the locale, fall back to English, then the
 *  raw key. Supports `{name}` interpolation. */
export function translate(locale: Locale, key: string, vars?: Record<string, string | number>): string {
  let s = messages[locale]?.[key] ?? messages.en[key] ?? key
  if (vars) for (const [k, v] of Object.entries(vars)) s = s.replaceAll(`{${k}}`, String(v))
  return s
}
