using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.App.Localization;

public static class UserFacingError
{
    public static string Localize(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            InvoiceNotFoundException => L("UserError.InvoiceNotFound.Message"),
            InvoiceAlreadyVoidedException => L("UserError.InvoiceAlreadyVoided.Message"),
            ArgumentException or DomainValidationException => L("UserError.InvalidInput.Message"),
            IOException or UnauthorizedAccessException => L("UserError.FileOperation.Message"),
            SqliteException or DbUpdateException => L("UserError.DataOperation.Message"),
            _ => L("UserError.Unexpected.Message"),
        };
    }

    private static string L(string key) => LocalizationState.GetString(key);
}
