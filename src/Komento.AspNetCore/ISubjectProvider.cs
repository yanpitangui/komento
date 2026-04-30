using Microsoft.AspNetCore.Http;

namespace Komento.AspNetCore;

public interface ISubjectProvider
{
    string  SubjectType { get; }
    string? GetSubject(HttpContext context);
}
