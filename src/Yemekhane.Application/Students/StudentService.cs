using Yemekhane.Application.Common;

namespace Yemekhane.Application.Students;

public sealed class StudentService(IStudentRepository repository)
{
    public Task<PagedResult<StudentListItem>> SearchAsync(StudentQuery query, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(query.Search) && query.Search.Trim().Length < 2)
            throw new RequestValidationException("Genel arama için en az 2 karakter girilmelidir.");
        if (query.Page < 1 || query.PageSize is < 1 or > 200)
            throw new RequestValidationException("Sayfa en az 1, sayfa boyutu 1-200 arasında olmalıdır.");
        return repository.SearchAsync(query, cancellationToken);
    }

    public async Task<StudentDetails> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await repository.GetAsync(id, cancellationToken)
        ?? throw new EntityNotFoundException("Öğrenci bulunamadı.");

    public async Task<StudentDetails> CreateAsync(SaveStudentRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAndValidate(request);
        if (await repository.StudentNoExistsAsync(normalized.StudentNo, null, cancellationToken))
            throw new EntityConflictException($"{normalized.StudentNo} numaralı öğrenci zaten kayıtlı.");
        var id = await repository.AddAsync(normalized, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<StudentDetails> UpdateAsync(Guid id, SaveStudentRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAndValidate(request);
        if (await repository.StudentNoExistsAsync(normalized.StudentNo, id, cancellationToken))
            throw new EntityConflictException($"{normalized.StudentNo} numaralı öğrenci zaten kayıtlı.");
        if (!await repository.UpdateAsync(id, normalized, cancellationToken))
            throw new EntityNotFoundException("Öğrenci bulunamadı.");
        return await GetAsync(id, cancellationToken);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await repository.SoftDeleteAsync(id, cancellationToken))
            throw new EntityNotFoundException("Öğrenci bulunamadı.");
    }

    private static SaveStudentRequest NormalizeAndValidate(SaveStudentRequest request)
    {
        var studentNo = request.StudentNo?.Trim() ?? string.Empty;
        var firstName = request.FirstName?.Trim() ?? string.Empty;
        var lastName = request.LastName?.Trim() ?? string.Empty;
        if (studentNo.Length is < 1 or > 32) throw new RequestValidationException("Öğrenci NO alanı 1-32 karakter olmalıdır.");
        if (firstName.Length is < 1 or > 100) throw new RequestValidationException("Ad alanı 1-100 karakter olmalıdır.");
        if (lastName.Length is < 1 or > 100) throw new RequestValidationException("Soyad alanı 1-100 karakter olmalıdır.");
        var nationalId = string.IsNullOrWhiteSpace(request.NationalId) ? null : request.NationalId.Trim();
        if (nationalId is not null && (nationalId.Length != 11 || nationalId.Any(c => !char.IsDigit(c))))
            throw new RequestValidationException("TC Kimlik No 11 rakamdan oluşmalıdır.");
        return request with { StudentNo = studentNo, FirstName = firstName, LastName = lastName, NationalId = nationalId };
    }
}
