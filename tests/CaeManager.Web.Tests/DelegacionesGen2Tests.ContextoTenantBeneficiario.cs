using System;
using System.Linq;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Hallazgo 16 de la revisión 1 a 1 del 2026-10-21: con la Gestora CAE del Operador CAE
/// externo en el contexto de un Tenant beneficiario delegado, <c>/delegaciones</c> enseñaba
/// todas las delegaciones del Operador CAE externo hacia otras Empresas gestionadas, y a un
/// Administrador del Operador CAE externo con el rol que guarda su cartera («Gestor CAE»).
///
/// <para>
/// <b>Lo que SÍ observa:</b> que la página pide la lista acotada al Tenant seleccionado (y
/// sin acotar en la organización propia) y qué rol pinta por persona. <b>Lo que NO observa:</b>
/// el filtro en base de datos (<c>ObtenerDelegacionesQueryTests</c>, Application.Tests).
/// </para>
/// </summary>
public partial class DelegacionesGen2Tests
{
    [Fact]
    public void Con_un_Tenant_beneficiario_seleccionado_pide_solo_las_delegaciones_que_lo_afectan()
    {
        var seleccionado = Guid.NewGuid();
        _tenantSeleccionado = seleccionado;

        var (_, mediador, _) = Renderizar(Delegacion(tenantClienteId: seleccionado));

        mediador.Enviadas.Select(e => e.Peticion).OfType<ObtenerDelegacionesQuery>()
            .Should().NotBeEmpty()
            .And.OnlyContain(q => q.TenantAfectadoId == seleccionado);
    }

    [Fact]
    public void En_la_organizacion_propia_pide_todas_las_delegaciones()
    {
        var (_, mediador, _) = Renderizar(Delegacion());

        mediador.Enviadas.Select(e => e.Peticion).OfType<ObtenerDelegacionesQuery>()
            .Should().NotBeEmpty()
            .And.OnlyContain(q => q.TenantAfectadoId == null);
    }

    [Fact]
    public void Un_Administrador_del_Operador_CAE_externo_figura_como_Administrador_aunque_su_cartera_guarde_Gestor_CAE()
    {
        var delegacion = Delegacion(rol: Roles.GestorCae);
        ConocerCuenta(delegacion.Operadores[0].UsuarioId, Roles.Administrador);

        var (cut, _, _) = Renderizar(delegacion);

        var rol = cut.Find(".delegaciones-tabla tbody td:nth-child(2)").TextContent.Trim();
        rol.Should().Be(Roles.NombreVisible(Roles.Administrador),
            "decisión 2026-10-20: el perfil de origen es el rol que ejerce en la Empresa gestionada");
    }

    [Fact]
    public void En_una_delegacion_de_Soporte_TALVEG_se_pinta_el_rol_de_la_delegacion()
    {
        var delegacion = Delegacion(soporte: true, rol: Roles.Consulta);
        ConocerCuenta(delegacion.Operadores[0].UsuarioId, Roles.Administrador);

        var (cut, _, _) = Renderizar(delegacion);

        cut.Find(".delegaciones-tabla tbody td:nth-child(2)").TextContent.Trim()
            .Should().Be(Roles.NombreVisible(Roles.Consulta), "en Soporte TALVEG decide la fila delegada, no el perfil de origen");
    }

    private void ConocerCuenta(Guid usuarioId, string rol) =>
        _cuentasConocidas[usuarioId] = (
            new ApplicationUser { Id = usuarioId, NombreCompleto = "Persona de prueba", Email = "persona@operador.test" },
            rol);
}
