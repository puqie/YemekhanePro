using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Common;
using Yemekhane.Application.Students;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/students")]
public sealed class StudentsController(StudentService service, StudentPhotoService photos) : ControllerBase
{
    [HttpGet]
    [PermissionAuthorize(Permissions.StudentsRead)]
    public async Task<PagedResult<StudentListItem>> Search([FromQuery] StudentQuery query, CancellationToken cancellationToken)
    {
        var result = await service.SearchAsync(query, cancellationToken);
        if (CanReadSensitive()) return result;
        return StudentSensitiveMasker.Mask(result);
    }

    [HttpGet("{id:guid}")]
    [PermissionAuthorize(Permissions.StudentsRead)]
    public async Task<StudentDetails> Get(Guid id, CancellationToken cancellationToken)
    {
        var value = await service.GetAsync(id, cancellationToken);
        return CanReadSensitive() ? value : StudentSensitiveMasker.Mask(value);
    }

    [HttpPost]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public async Task<ActionResult<StudentDetails>> Create(SaveStudentRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id },
            CanReadSensitive() ? created : StudentSensitiveMasker.Mask(created));
    }

    [HttpPut("{id:guid}")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public async Task<StudentDetails> Update(Guid id, SaveStudentRequest request, CancellationToken cancellationToken)
    {
        var updated = await service.UpdateAsync(id, request, cancellationToken);
        return CanReadSensitive() ? updated : StudentSensitiveMasker.Mask(updated);
    }

    [HttpDelete("{id:guid}")]
    [PermissionAuthorize(Permissions.StudentsDeactivate)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        await service.DeactivateAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Sicil karti fotografi (eski programdaki "Resim Sec"). multipart "file", en fazla 2 MB,
    /// JPG/PNG; dosya veri klasorundeki photos/ altina yazilir, kayitta goreli yol tutulur.
    /// </summary>
    [HttpPost("{id:guid}/photo")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    [RequestSizeLimit(2_500_000)]
    public async Task<StudentDetails> UploadPhoto(Guid id, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null) throw new RequestValidationException("Fotoğraf dosyası gönderilmedi.");
        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        stream.Position = 0;
        var updated = await photos.UploadAsync(id, file.FileName, file.Length, stream, cancellationToken);
        return CanReadSensitive() ? updated : StudentSensitiveMasker.Mask(updated);
    }

    [HttpGet("{id:guid}/photo")]
    [PermissionAuthorize(Permissions.StudentsRead)]
    public async Task<IActionResult> GetPhoto(Guid id, CancellationToken cancellationToken)
    {
        var photo = await photos.GetAsync(id, cancellationToken);
        return File(photo.Content, photo.ContentType);
    }

    [HttpDelete("{id:guid}/photo")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public async Task<IActionResult> DeletePhoto(Guid id, CancellationToken cancellationToken)
    {
        await photos.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    private bool CanReadSensitive() => User.HasClaim(Permissions.ClaimType, Permissions.StudentsSensitiveRead);
}

public static class StudentSensitiveMasker
{
    public static PagedResult<StudentListItem> Mask(PagedResult<StudentListItem> result) => result with
    {
        Items = result.Items.Select(x => x with { ParentPhone = MaskPhone(x.ParentPhone) }).ToArray()
    };

    public static StudentDetails Mask(StudentDetails value) => value with
    {
        NationalId = MaskAll(value.NationalId), FingerprintId = MaskAll(value.FingerprintId),
        Pid = MaskAll(value.Pid), Address = value.Address is null ? null : "••••••"
    };

    private static string? MaskAll(string? value) => string.IsNullOrEmpty(value) ? value : new string('•', value.Length);
    public static string? MaskPhone(string? value) => string.IsNullOrEmpty(value) ? value
        : value.Length <= 4 ? new string('•', value.Length) : new string('•', value.Length - 4) + value[^4..];
}
