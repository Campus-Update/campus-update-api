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
[Authorize(Roles = "SchoolAdmin,SuperAdmin")]
[Route("api/v1/departments")]
public sealed class DepartmentsController(CampusUpdateDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<DepartmentAdminResponse>>> GetDepartments(
        [FromQuery] Guid? institutionId,
        CancellationToken cancellationToken)
    {
        var user = await CurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (user.Role == UserRole.SchoolAdmin)
        {
            if (institutionId.HasValue && institutionId.Value != user.InstitutionId) return Forbid();
            institutionId = user.InstitutionId;
        }

        var query = db.Departments.AsNoTracking()
            .Include(x => x.Faculty).ThenInclude(x => x.Institution)
            .Include(x => x.Programmes).ThenInclude(x => x.Levels)
            .AsQueryable();
        if (institutionId.HasValue)
            query = query.Where(x => x.Faculty.InstitutionId == institutionId.Value);

        var departments = await query.OrderBy(x => x.Faculty.Institution.Name)
            .ThenBy(x => x.Faculty.Name).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        return Ok(departments.Select(ToResponse).ToArray());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DepartmentAdminResponse>> GetDepartment(Guid id, CancellationToken cancellationToken)
    {
        var user = await CurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        var department = await db.Departments.AsNoTracking()
            .Include(x => x.Faculty).ThenInclude(x => x.Institution)
            .Include(x => x.Programmes).ThenInclude(x => x.Levels)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (department is null ||
            (user.Role == UserRole.SchoolAdmin && department.Faculty.InstitutionId != user.InstitutionId))
            return NotFound();
        return Ok(ToResponse(department));
    }

    private async Task<AppUser?> CurrentUser(CancellationToken cancellationToken) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.IsActive, cancellationToken)
            : null;

    private static DepartmentAdminResponse ToResponse(Department department) => new(
        department.Id,
        department.Name,
        department.Code,
        department.FacultyId,
        department.Faculty.Name,
        department.Faculty.InstitutionId,
        department.Faculty.Institution.Name,
        department.Programmes.OrderBy(x => x.Name).Select(programme => new ProgrammeResponse(
            programme.Id,
            programme.Name,
            programme.Code,
            programme.Levels.OrderBy(x => x.SortOrder)
                .Select(level => new AcademicLevelResponse(level.Id, level.Name, level.SortOrder)).ToArray()
        )).ToArray());
}
