using System.Security.Claims;
using Komento.AspNetCore;
using Microsoft.AspNetCore.Http;

namespace Komento.Sample.EcommerceApi.Komento;

internal sealed class JwtSubjectProvider : ISubjectProvider
{
    public string  SubjectType => "user";
    public string? GetSubject(HttpContext context)
        => context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? context.User.FindFirst("sub")?.Value;
}
