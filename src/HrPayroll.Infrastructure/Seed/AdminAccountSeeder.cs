using HrPayroll.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HrPayroll.Infrastructure.Seed;

/// <summary>
/// المصدر الوحيد لإنشاء حساب المسؤول الأول: قسم AdminAccount في appsettings.json.
/// idempotent: يُنشئ الدور والحساب إن لم يوجدا، ولا يعيد تعيين كلمة المرور إلا إذا لم تطابق القيمة المضبوطة.
/// لا يتعامل مع PasswordHash مباشرة ولا يسجّل كلمة المرور.
/// </summary>
public static class AdminAccountSeeder
{
    public const string SectionName = "AdminAccount";

    public static async Task SeedAsync(
        IConfiguration configuration,
        RoleManager<IdentityRole> roleManager,
        UserManager<ApplicationUser> userManager,
        ILogger logger)
    {
        var section = configuration.GetSection(SectionName);
        var email = section["Email"]?.Trim();
        var password = section["Password"];
        var fullName = section["FullName"]?.Trim();

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(fullName))
            throw new InvalidOperationException(
                "إعدادات حساب المسؤول ناقصة: تأكد من وجود AdminAccount:Email و AdminAccount:Password و AdminAccount:FullName في appsettings.json.");

        if (!await roleManager.RoleExistsAsync(AppRoles.Admin))
        {
            var roleResult = await roleManager.CreateAsync(new IdentityRole(AppRoles.Admin));
            if (!roleResult.Succeeded)
                throw new InvalidOperationException(
                    "تعذّر إنشاء دور المسؤول: " + string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        }

        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = fullName,
                IsActive = true
            };
            var createResult = await userManager.CreateAsync(user, password);
            if (!createResult.Succeeded)
                throw new InvalidOperationException(
                    "تعذّر إنشاء حساب المسؤول: " + string.Join("; ", createResult.Errors.Select(e => e.Description)));
            logger.LogInformation("تم إنشاء حساب المسؤول {Email}.", email);
        }
        else
        {
            var changed = false;
            if (!user.IsActive) { user.IsActive = true; changed = true; }
            if (string.IsNullOrWhiteSpace(user.FullName)) { user.FullName = fullName; changed = true; }
            if (changed)
            {
                var update = await userManager.UpdateAsync(user);
                if (!update.Succeeded)
                    throw new InvalidOperationException(
                        "تعذّر تحديث حساب المسؤول: " + string.Join("; ", update.Errors.Select(e => e.Description)));
            }

            // كلمة المرور تُعاد تعيينها فقط إذا لم تطابق القيمة المضبوطة (لا في كل تشغيل).
            if (!await userManager.CheckPasswordAsync(user, password))
            {
                var token = await userManager.GeneratePasswordResetTokenAsync(user);
                var reset = await userManager.ResetPasswordAsync(user, token, password);
                if (!reset.Succeeded)
                    throw new InvalidOperationException(
                        "تعذّر تعيين كلمة مرور المسؤول: " + string.Join("; ", reset.Errors.Select(e => e.Description)));
                logger.LogInformation("تمت مزامنة كلمة مرور حساب المسؤول {Email} مع الإعدادات.", email);
            }
        }

        if (!await userManager.IsInRoleAsync(user, AppRoles.Admin))
        {
            var roleAdd = await userManager.AddToRoleAsync(user, AppRoles.Admin);
            if (!roleAdd.Succeeded)
                throw new InvalidOperationException(
                    "تعذّر منح دور المسؤول: " + string.Join("; ", roleAdd.Errors.Select(e => e.Description)));
        }
    }
}
