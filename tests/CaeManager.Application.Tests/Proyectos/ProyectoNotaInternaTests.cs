using CaeManager.Application.Plataforma;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectoPorId;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Proyectos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Proyectos;

/// <summary>
/// La «Nota interna» de Proyecto 360 la decide la política única (<c>IPoliticaNotaInterna</c>), igual que en el resto
/// de fichas 360: el equipo la lee, la Sesión Privilegiada de acceso total también, y el rol Cliente no.
/// </summary>
public class ProyectoNotaInternaTests
{
    private const string Nota = "Entrar por la puerta de carga.";

    /// <summary>Proyectos con proveedor asíncrono: el handler usa <c>FirstOrDefaultAsync</c>.</summary>
    private sealed class ProyectosAsincronos(Proyecto proyecto) : IProyectosQueryContext
    {
        public IQueryable<Proyecto> Proyectos => new TestAsyncQueryable<Proyecto>(new[] { proyecto }.AsQueryable());
        public IQueryable<ProyectoTecnico> ProyectosTecnicos => new TestAsyncQueryable<ProyectoTecnico>(Array.Empty<ProyectoTecnico>().AsQueryable());
    }

    private static async Task<ProyectoDetalleDto?> LeerAsync(string? rol, SesionPrivilegiadaActiva? sesion = null)
    {
        var empresas = new EmpresasQueryContextFalso();
        var cliente = Empresa.CrearComoCliente("Refrielectric S.A.", "B10380392", false, null, null);
        empresas.ListaEmpresas.Add(cliente);

        var centros = new CentrosQueryContextFalso();
        var centro = new Centro(cliente.Id, cliente.Id, "Planta Sevilla");
        centros.ListaCentros.Add(centro);

        var proyecto = Proyecto.Crear(cliente.Id, centro.Id, "Obra norte", new DateOnly(2026, 1, 10), null, Nota);

        var handler = new ObtenerProyectoPorIdQueryHandler(
            centros, empresas, new DocumentosQueryContextFalso(), new ProyectosAsincronos(proyecto), new AlcanceDatosServiceFalso(),
            PoliticaNotaInternaPruebas.Con(rol, sesion));

        return await handler.Handle(new ObtenerProyectoPorIdQuery(proyecto.Id), CancellationToken.None);
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task El_equipo_recibe_la_nota(string rol)
    {
        var dto = await LeerAsync(rol);

        dto!.Notas.Should().Be(Nota);
        dto.NotaInternaVisible.Should().BeTrue();
    }

    [Fact]
    public async Task El_usuario_de_Cliente_no_recibe_la_nota()
    {
        var dto = await LeerAsync("Cliente");

        dto.Should().NotBeNull("el proyecto sí se lee: el corte es solo de la nota");
        dto!.Notas.Should().BeNull();
        dto.NotaInternaVisible.Should().BeFalse();
    }

    /// <summary>Soporte TALVEG con Sesión Privilegiada de acceso total: sin rol de negocio, recibe la nota como el equipo.</summary>
    [Fact]
    public async Task Sesion_privilegiada_con_acceso_total_recibe_la_nota_sin_rol()
    {
        var dto = await LeerAsync(null, PoliticaNotaInternaPruebas.Sesion(CapacidadPrivilegio.SoporteLectura));

        dto!.Notas.Should().Be(Nota);
        dto.NotaInternaVisible.Should().BeTrue();
    }
}
