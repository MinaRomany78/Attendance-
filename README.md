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
tests/HrPayroll.Tests         151 اختبارًا (حسابات، استيراد، تقارير، أمان)
```

## المتطلبات

- .NET SDK 9.0
- SQL Server (خادم بعيد أو محلي) — التطبيق لا يستخدم SQLite أو InMemory في الإنتاج
- `dotnet-ef` لتشغيل أو إنشاء الترحيلات

## إعداد سلسلة الاتصال

المفتاح: `ConnectionStrings:DefaultConnection`.
لا تُخزَّن كلمة المرور في المستودع؛ `appsettings.json` يحتوي على قيمة بديلة `__SET_PASSWORD__`.

الأولوية عند القراءة: متغيّر البيئة `ConnectionStrings__DefaultConnection` ← User Secrets (Development) ← `appsettings.Production.json` ← `appsettings.json`.

```bash
# Windows PowerShell
export ConnectionStrings__DefaultConnection="Server=HOST;Database=DB;User Id=USER;Password=***;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=True;"
```

أو للتطوير:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "..." --project src/HrPayroll.Web
```

## الترحيلات وقاعدة البيانات

سلسلة الترحيلات الحالية:

1. `20261012085017_InitialCreate`
2. `20261013064441_HrUpgrade_Employees_Shifts_Attendance_Payroll`
3. `20261013130424_PayrollSheetEditing_LeaveBalances`
4. `20261027093747_HrUpgrade_Advances_Expenses_Repayments`

```bash
dotnet ef migrations list --project src/HrPayroll.Infrastructure --startup-project src/HrPayroll.Web
dotnet ef database update --project src/HrPayroll.Infrastructure --startup-project src/HrPayroll.Web
```

`Database:ApplyMigrationsOnStartup=true` يطبّق الترحيلات الناقصة عند بدء التشغيل (لا يحذف بيانات ولا يُنشئ قاعدة جديدة).

## التشغيل

```bash
dotnet restore
dotnet build --configuration Release
dotnet run --project src/HrPayroll.Web
```

على Windows يمكن استخدام `run.bat`، وللنشر `publish.bat`.

## أول مسؤول (بدون كلمات مرور ثابتة)

في الإنتاج لا يُنشأ أي مستخدم تلقائيًا. مرّر بيانات أول مسؤول مرة واحدة:

```bash
export SeedAdmin__Email="owner@company.com"
export SeedAdmin__Password="<كلمة مرور قوية>"
export SeedAdmin__FullName="مالك النظام"
```

في بيئة Development يُنشأ حساب إداري بدون كلمة مرور معروفة في الكود (تُضبط عبر User Secrets).

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

## الاختبارات

```bash
dotnet test tests/HrPayroll.Tests/HrPayroll.Tests.csproj --configuration Release
```

151 اختبارًا تغطي: حساب الحضور والورديات الليلية، الرواتب والإضافي والغياب،
منع الخصم المزدوج، الإقفال وإعادة الفتح، الاستيراد والتصدير، التقارير، والأمان.
