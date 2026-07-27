using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.Validation;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Repositories;

public sealed class CompanyProfileRepository(MhcDbContext context) : ICompanyProfileRepository
{
    private const int SingletonId = 1;

    public async Task<VersionedCompanyProfile?> GetAsync(CancellationToken cancellationToken = default)
    {
        CompanyProfileEntity? entity = await context.CompanyProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(profile => profile.Id == SingletonId, cancellationToken);
        return entity is null ? null : ToVersioned(entity);
    }

    public async Task<VersionedCompanyProfile> SaveAsync(
        CompanyProfileSettings profile,
        int? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        CompanyProfileSettings normalized = Validate(profile);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (expectedRevision is null)
        {
            CompanyProfileEntity entity = new()
            {
                Id = SingletonId,
                Revision = 0,
                NameArabic = normalized.NameArabic,
                NameEnglish = normalized.NameEnglish,
                VatNumber = normalized.VatNumber,
                CommercialRegistration = normalized.CommercialRegistration,
                Branch = normalized.Branch,
                Address = normalized.Address,
                OperatorName = normalized.OperatorName,
                DefaultPaymentMethod = normalized.DefaultPaymentMethod,
                LogoBytes = normalized.LogoBytes?.ToArray(),
                LogoMimeType = normalized.LogoMimeType,
                CreatedAtUtcMs = now,
                UpdatedAtUtcMs = now,
            };
            context.CompanyProfiles.Add(entity);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
            {
                throw new PersistenceConcurrencyException(
                    "The company profile was created by another operation.",
                    exception);
            }
            finally
            {
                context.Entry(entity).State = EntityState.Detached;
            }

            return new VersionedCompanyProfile(normalized, 0);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision.Value);
        int nextRevision = checked(expectedRevision.Value + 1);
        int updated = await context.CompanyProfiles
            .Where(entity => entity.Id == SingletonId && entity.Revision == expectedRevision.Value)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(entity => entity.NameArabic, normalized.NameArabic)
                .SetProperty(entity => entity.NameEnglish, normalized.NameEnglish)
                .SetProperty(entity => entity.VatNumber, normalized.VatNumber)
                .SetProperty(entity => entity.CommercialRegistration, normalized.CommercialRegistration)
                .SetProperty(entity => entity.Branch, normalized.Branch)
                .SetProperty(entity => entity.Address, normalized.Address)
                .SetProperty(entity => entity.OperatorName, normalized.OperatorName)
                .SetProperty(entity => entity.DefaultPaymentMethod, normalized.DefaultPaymentMethod)
                .SetProperty(entity => entity.LogoBytes, normalized.LogoBytes)
                .SetProperty(entity => entity.LogoMimeType, normalized.LogoMimeType)
                .SetProperty(entity => entity.Revision, nextRevision)
                .SetProperty(entity => entity.UpdatedAtUtcMs, now),
                cancellationToken);
        if (updated != 1)
        {
            throw new PersistenceConcurrencyException(
                "The company profile was modified or deleted by another operation.",
                new DbUpdateConcurrencyException("No company profile matched the expected revision."));
        }

        return new VersionedCompanyProfile(normalized, nextRevision);
    }

    private static CompanyProfileSettings Validate(CompanyProfileSettings profile)
    {
        PartySnapshot seller = PartySnapshot.Create(
            profile.NameArabic,
            profile.NameEnglish,
            profile.VatNumber,
            profile.CommercialRegistration,
            profile.Address);
        if (seller.VatNumber is null)
        {
            throw new ArgumentException("A 15-digit seller VAT number is required.", nameof(profile));
        }

        string branch = Required(profile.Branch, DomainFieldLimits.PartyName, nameof(profile.Branch));
        string address = Required(seller.Address, DomainFieldLimits.Address, nameof(profile.Address));
        string operatorName = Required(profile.OperatorName, DomainFieldLimits.PartyName, nameof(profile.OperatorName));
        if (!Enum.IsDefined(profile.DefaultPaymentMethod))
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "The default payment method is invalid.");
        }

        byte[]? logo = profile.LogoBytes?.ToArray();
        string? logoMimeType = string.IsNullOrWhiteSpace(profile.LogoMimeType)
            ? null
            : profile.LogoMimeType.Trim().ToLowerInvariant();
        if (logo is { Length: > 2_000_000 })
        {
            throw new ArgumentException("The company logo cannot exceed 2 MB.", nameof(profile));
        }

        if ((logo is null) != (logoMimeType is null) ||
            logoMimeType is not (null or "image/png" or "image/jpeg"))
        {
            throw new ArgumentException("The company logo must be a PNG or JPEG image.", nameof(profile));
        }

        return profile with
        {
            NameArabic = seller.NameArabic,
            NameEnglish = seller.NameEnglish,
            VatNumber = seller.VatNumber,
            CommercialRegistration = seller.CommercialRegistration,
            Branch = branch,
            Address = address,
            OperatorName = operatorName,
            LogoBytes = logo,
            LogoMimeType = logoMimeType,
        };
    }

    private static string Required(string? value, int maxLength, string parameterName)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maxLength)
        {
            throw new ArgumentException($"A value between 1 and {maxLength} characters is required.", parameterName);
        }

        return normalized;
    }

    private static VersionedCompanyProfile ToVersioned(CompanyProfileEntity entity) => new(
        new CompanyProfileSettings(
            entity.NameArabic,
            entity.NameEnglish,
            entity.VatNumber,
            entity.CommercialRegistration,
            entity.Branch,
            entity.Address,
            entity.OperatorName,
            entity.DefaultPaymentMethod,
            entity.LogoBytes?.ToArray(),
            entity.LogoMimeType),
        entity.Revision);
}
