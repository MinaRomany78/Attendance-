# نظام إدارة الموارد البشرية والحضور والرواتب

تطبيق ASP.NET Core MVC (.NET 9) لإدارة الموظفين والأقسام، بصمات الحضور والورديات،
الرواتب الشهرية، السلف والمصروفات والقروض، الإجازات، والتقارير مع تصدير Excel و PDF.

## التقنيات

- .NET 9 / ASP.NET Core MVC / Razor Views (RTL عربي)
- EF Core 9 + SQL Server (`Microsoft.EntityFrameworkCore.SqlServer`)
- ASP.NET Core Identity (Admin, HR, Accountant, Viewer)
- ClosedXML و ExcelDataReader (استيراد/تصدير Excel) و QuestPDF (PDF)
- xUnit للاختبارات

## هيكل المشروع

```
HrPayroll.sln
src/HrPayroll.Domain          الكيانات، التعدادات، خدمات الحساب النقية
src/HrPayroll.Infrastructure  EF Core، الترحيلات، الخدمات، الاستيراد والتصدير
src/HrPayroll.Web             MVC Controllers + Views + ViewModels + static assets
tests/HrPayroll.Tests         166 اختبارًا (حسابات، استيراد، تقارير، أمان)
```

## المتطلبات

- .NET 9 SDK
- SQL Server محلي (Express أو Developer أو غيرها) مع Windows Authentication — لا حاجة لأي خادم بعيد أو اتصال إنترنت لقاعدة البيانات
- `dotnet-ef` (`dotnet tool install --global dotnet-ef --version 9.0.*`)

## قاعدة البيانات: SQL Server محلي فقط

التطبيق يتصل بـ SQL Server المثبّت على جهازك باستخدام Windows Authentication. لا يوجد اتصال بعيد أو احتياطي.

سلسلة الاتصال في `src/HrPayroll.Web/appsettings.json`:

```json
"ConnectionStrings": {
  "DefaultConnection": "Server=localhost;Database=HrPayrollDb;Trusted_Connection=True;TrustServerCertificate=True;"
}
```

- إذا كان لديك نسخة مُسمّاة (مثل `SQLEXPRESS`) غيّر `Server` إلى `.\SQLEXPRESS`.
- اسم قاعدة البيانات `HrPayrollDb` ينشئه أمر الترحيل تلقائيًا إن لم يكن موجودًا (لا يحذف ولا يُسقط أي قاعدة).
- قبل التشغيل تأكد أن خدمة SQL Server تعمل وأن حسابك لديه صلاحية إنشاء قاعدة بيانات.

## حساب المسؤول الأول (AdminAccount)

يُنشأ حساب المسؤول من القسم `AdminAccount` في `appsettings.json` عند بدء التشغيل، وهو المصدر الوحيد لهذا الحساب:

```json
"AdminAccount": {
  "Email": "admin@hr.local",
  "Password": "Admin123+",
  "FullName": "مدير النظام"
}
```

- إن لم يوجد الحساب يُنشأ مع دور `Admin`. وإن وُجد يُحتفظ بالمعرّف نفسه ويُضاف إليه الدور ويُفعَّل، دون حذف أو تكرار.
- تُعاد تعيين كلمة المرور فقط إذا لم تطابق القيمة المضبوطة، ولا تُكتب كلمة المرور في السجلات.
- **غيّر كلمة المرور الافتراضية** قبل أي استخدام حقيقي: عدّل `AdminAccount:Password` ثم أعد التشغيل، أو غيّرها من داخل النظام بعد الدخول.
- لا تضع كلمة مرور حقيقية في مستودع عام. استخدم ملف إعدادات محلي غير متتبَّع أو مخزن أسرار الخادم.

## الترحيلات وقاعدة البيانات

سلسلة الترحيلات الحالية (ترحيل مدمج واحد ينشئ كامل المخطط: الموظفين، الورديات، الحضور، الرواتب، السلف، المصروفات، القروض، الإجازات):

1. `20261031101003_initalCreate`

```bash
dotnet ef migrations list --project src/HrPayroll.Infrastructure --startup-project src/HrPayroll.Web
dotnet ef database update --project src/HrPayroll.Infrastructure --startup-project src/HrPayroll.Web
```

`Database:ApplyMigrationsOnStartup=true` يطبّق الترحيلات الناقصة عند بدء التشغيل (لا يحذف بيانات ولا يُنشئ قاعدة جديدة بالقوة).

## أوامر التشغيل على Windows (PowerShell)

```powershell
dotnet restore
dotnet build HrPayroll.sln --configuration Release
dotnet ef database update --project src/HrPayroll.Infrastructure --startup-project src/HrPayroll.Web
dotnet run --project src/HrPayroll.Web
```

للتحقق من الاتصال بقاعدة البيانات:

```powershell
sqlcmd -S localhost -E -Q "SELECT DB_NAME()"
```

ملاحظة: إذا استخدمت نسخة مُسمّاة استبدل `localhost` بـ `localhost\SQLEXPRESS`.

## الاختبارات

```powershell
dotnet test --configuration Release
```

الاختبارات تستخدم SQLite في الذاكرة ولا تحتاج SQL Server.

على Windows يمكن استخدام `run.bat`، وللنشر `publish.bat`.

```bash
dotnet test tests/HrPayroll.Tests/HrPayroll.Tests.csproj --configuration Release
```

## الحضور والورديات

- ثلاثة ورديات قابلة للضبط، والموظف غير مقيّد بوردة واحدة: تُحدَّد الوردة من البصمة نفسها.
- استيراد ملفي الدخول والخروج منفصلين، والقرن بينهما بالكود والوقت لا بترتيب الصفوف.
- كشف التكرار بالبصمة (`DedupHash`) فلا تُضاف بصمة مرتين عند إعادة رفع نفس الملف.
- حساب التأخير والغياب والإضافي وفترة السماح من إعدادات قابلة للضبط.
- الورديات الليلية تُنسب لتاريخ بداية الوردة.

## الرواتب

- الأجر اليومي = الراتب الأساسي ÷ 30، والأجر بالساعة = الأجر اليومي ÷ ساعات العمل.
- الإضافي = الساعات المعتمدة × الأجر بالساعة (بدون مضاعف) مع تقريب واحد على إجمالي الدقائق.
- خصم التأخير بالدقيقة، والغياب/الإجازة بدون أجر بالأجر اليومي، والإجازة السنوية مدفوعة.
- السلف والخصومات والمكافآت والبدلات والأيام الإضافية تُطبَّق مرة واحدة لكل فترة.
- إقفال الكشف يمنع إعادة الاحتساب؛ التصحيح يتطلب إعادة فتح بسبب مُسجَّل في سجل التدقيق.

## الأمان

- كل صفحة إدارية محمية بـ `[Authorize]` وسياسات أدوار (Admin / HR / Accountant / Viewer).
- حماية CSRF على كل النماذج، والتحقق من نوع وحجم الملفات المرفوعة.
- لا تُسجَّل كلمات المرور أو بيانات الاتصال في السجلات.

تغطي الاختبارات (166 اختبارًا): حساب الحضور والورديات الليلية، الرواتب والإضافي والغياب،
منع الخصم المزدوج، الإقفال وإعادة الفتح، الاستيراد والتصدير، التقارير، والأمان.
