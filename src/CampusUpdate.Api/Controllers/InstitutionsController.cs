using System.Security.Claims;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Authorize(Roles = "SchoolAdmin,LeadSchoolAdmin,NewsAdmin,CalendarAdmin,SuperAdmin")]
[Route("api/v1/institutions")]
public sealed class InstitutionsController(CampusUpdateDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<InstitutionAdminResponse>>> GetInstitutions(
        [FromQuery] Guid? institutionId,
        CancellationToken cancellationToken)
    {
        var user = await CurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (user.Role != UserRole.SuperAdmin)
        {
            if (institutionId.HasValue && institutionId.Value != user.InstitutionId) return Forbid();
            institutionId = user.InstitutionId;
        }

        var query = InstitutionGraph();
        if (institutionId.HasValue)
            query = query.Where(institution => institution.Id == institutionId.Value);

        var institutions = await query.OrderBy(institution => institution.Name)
            .ToListAsync(cancellationToken);
        return Ok(institutions.Select(ToResponse).ToArray());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InstitutionAdminResponse>> GetInstitution(
        Guid id, CancellationToken cancellationToken)
    {
        var user = await CurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        var institution = await InstitutionGraph()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (institution is null ||
            (user.Role != UserRole.SuperAdmin && institution.Id != user.InstitutionId))
            return NotFound();
        return Ok(ToResponse(institution));
    }

    private IQueryable<Institution> InstitutionGraph() => db.Institutions.AsNoTracking()
        .Include(institution => institution.Faculties)
            .ThenInclude(faculty => faculty.Departments)
                .ThenInclude(department => department.Programmes)
                    .ThenInclude(programme => programme.Levels);

    private async Task<AppUser?> CurrentUser(CancellationToken cancellationToken) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await db.Users.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id && x.IsActive, cancellationToken)
            : null;

    private static InstitutionAdminResponse ToResponse(Institution institution) => new(
        institution.Id,
        institution.Name,
        institution.Slug,
        institution.Acronym,
        institution.LogoUrl,
        institution.IsActive,
        institution.Faculties.OrderBy(faculty => faculty.Name).Select(faculty => new FacultyResponse(
            faculty.Id,
            faculty.Name,
            faculty.Code,
            faculty.Departments.OrderBy(department => department.Name).Select(department => new DepartmentResponse(
                department.Id,
                department.Name,
                department.Code,
                department.Programmes.OrderBy(programme => programme.Name).Select(programme => new ProgrammeResponse(
                    programme.Id,
                    programme.Name,
                    programme.Code,
                    programme.Levels.OrderBy(level => level.SortOrder)
                        .Select(level => new AcademicLevelResponse(level.Id, level.Name, level.SortOrder)).ToArray()
                )).ToArray()
            )).ToArray()
        )).ToArray());
}
