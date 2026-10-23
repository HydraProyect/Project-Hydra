using Bunit;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class PestanaHistorialPuertaAccesoDatosTests
{
    [Theory]
    [InlineData("Cliente")]
    [InlineData("Empresa")]
    [InlineData("Subcontrata")]
    [InlineData("Centro")]
    [InlineData("Trabajador")]
    [InlineData("Vehiculo")]
    [InlineData("Maquinaria")]
    public void El_historial_compartido_identifica_persona_Sistema_y_cuenta_eliminada_con_iniciales(string tipo)
    {
        var personaId = Guid.NewGuid();
        var eliminadoId = Guid.NewGuid();
        var entidadId = Guid.NewGuid();
        var almacen = new AlmacenUsuarios(new()
        {
            [personaId.ToString()] = new ApplicationUser { Id = personaId, NombreCompleto = "Marta Arcos" },
        });
        RegistroAuditoriaListaDto Fila(Guid? usuarioId) =>
            new(Guid.NewGuid(), tipo, entidadId, "Modificado", usuarioId, DateTime.UtcNow, false, false);
        Services.AddScoped<IMediator>(_ => new MediatorHistorial([Fila(personaId), Fila(null), Fila(eliminadoId)]));
        Services.AddLocalization();
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            almacen, null!, null!, null!, null!, null!, null!, null!, null!));

        var cut = Render<PestanaHistorial>(p => p.Add(x => x.EntidadTipo, tipo).Add(x => x.EntidadId, entidadId));

        cut.FindAll(".workspace-historial-usuario").Select(e => e.TextContent)
            .Should().Equal("Marta Arcos", "Sistema", "(usuario eliminado)");
        cut.FindAll(".workspace-historial-autor .avatar-usuario").Select(e => e.TextContent.Trim())
            .Should().Equal("MA", "S", "—");
        cut.FindComponents<AvatarUsuario>().Should().HaveCount(3)
            .And.OnlyContain(a => a.Instance.UsarIniciales && a.Instance.Tamano == TamanoAvatarUsuario.Pequeno);
        almacen.Buscados.Should().BeEquivalentTo([personaId.ToString(), eliminadoId.ToString()]);
    }
}