using CaeManager.Application.Blindaje42.Queries.ObtenerBlindajeEmpresasDeCliente;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Blindaje42;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Blindaje42;

public class ObtenerBlindajeEmpresasDeClienteQueryHandlerTests
{
    private static readonly Guid ClienteId = Guid.NewGuid();

    private sealed class Contexto
    {
        public EmpresasQueryContextFalso EmpresasContext { get; } = new();
        public Blindaje42QueryContextFalso BlindajeContext { get; } = new();

        public ObtenerBlindajeEmpresasDeClienteQueryHandler CrearHandler(AlcanceDatosServiceFalso? alcance = null) =>
            new(EmpresasContext, BlindajeContext, alcance ?? new AlcanceDatosServiceFalso());
    }

    private static Empresa CrearEmpresaContratista(string razonSocial = "Contratista de prueba") =>
        new(razonSocial, null);

    [Fact]
    public async Task Devuelve_vacio_cuando_el_cliente_no_es_visible()
    {
        var contexto = new Contexto();
        var handler = contexto.CrearHandler(new AlcanceDatosServiceFalso(tieneAccesoTotal: false));

        var resultado = await handler.Handle(new ObtenerBlindajeEmpresasDeClienteQuery(ClienteId), CancellationToken.None);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Incluye_una_empresa_con_relacion_vigente_sin_solicitudes()
    {
        var contexto = new Contexto();
        var empresa = CrearEmpresaContratista();
        contexto.EmpresasContext.ListaEmpresas.Add(empresa);
        contexto.EmpresasContext.ListaRelacionesEmpresariales.Add(
            RelacionEmpresarial.Crear(empresa.Id, ClienteId, DateTime.UtcNow.AddYears(-1)));
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(new ObtenerBlindajeEmpresasDeClienteQuery(ClienteId), CancellationToken.None);

        resultado.Should().ContainSingle(e => e.EmpresaId == empresa.Id && e.UltimaSolicitud == null);
    }

    /// <summary>El historial no desaparece solo porque la relación ya cerró — ver el comentario de la query.</summary>
    [Fact]
    public async Task Incluye_una_empresa_con_relacion_ya_cerrada_si_tiene_solicitudes()
    {
        var contexto = new Contexto();
        var empresa = CrearEmpresaContratista();
        contexto.EmpresasContext.ListaEmpresas.Add(empresa);
        var relacion = RelacionEmpresarial.Crear(empresa.Id, ClienteId, DateTime.UtcNow.AddYears(-2));
        relacion.Cerrar(DateTime.UtcNow.AddYears(-1));
        contexto.EmpresasContext.ListaRelacionesEmpresariales.Add(relacion);
        contexto.BlindajeContext.ListaSolicitudes.Add(
            new SolicitudCertificacionTgss(empresa.Id, ClienteId, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)), Guid.NewGuid()));
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(new ObtenerBlindajeEmpresasDeClienteQuery(ClienteId), CancellationToken.None);

        resultado.Should().ContainSingle(e => e.EmpresaId == empresa.Id && e.TotalSolicitudes == 1);
    }

    /// <summary>
    /// El caso que motivó el filtro: un gestor con cartera restringida no
    /// puede ver, por la vía del historial de solicitudes, una Empresa cuya
    /// única relación con el Cliente ya cerró y que por tanto no forma parte
    /// de su ObtenerEmpresaIdsVisiblesAsync (que se deriva de relaciones
    /// VIGENTES). Sin el filtro, esta prueba falla mostrando la fila.
    /// </summary>
    [Fact]
    public async Task Oculta_el_historial_de_una_empresa_fuera_de_la_cartera_restringida()
    {
        var contexto = new Contexto();
        var empresa = CrearEmpresaContratista();
        contexto.EmpresasContext.ListaEmpresas.Add(empresa);
        var relacion = RelacionEmpresarial.Crear(empresa.Id, ClienteId, DateTime.UtcNow.AddYears(-2));
        relacion.Cerrar(DateTime.UtcNow.AddYears(-1));
        contexto.EmpresasContext.ListaRelacionesEmpresariales.Add(relacion);
        contexto.BlindajeContext.ListaSolicitudes.Add(
            new SolicitudCertificacionTgss(empresa.Id, ClienteId, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)), Guid.NewGuid()));

        // Cartera restringida que sí ve al Cliente pero NO a esta empresa (su única relación ya cerró).
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [ClienteId], empresaIdsVisibles: []);
        var handler = contexto.CrearHandler(alcance);

        var resultado = await handler.Handle(new ObtenerBlindajeEmpresasDeClienteQuery(ClienteId), CancellationToken.None);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Muestra_la_empresa_con_relacion_vigente_si_esta_en_la_cartera_restringida()
    {
        var contexto = new Contexto();
        var empresa = CrearEmpresaContratista();
        contexto.EmpresasContext.ListaEmpresas.Add(empresa);
        contexto.EmpresasContext.ListaRelacionesEmpresariales.Add(
            RelacionEmpresarial.Crear(empresa.Id, ClienteId, DateTime.UtcNow.AddYears(-1)));

        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [ClienteId], empresaIdsVisibles: [empresa.Id]);
        var handler = contexto.CrearHandler(alcance);

        var resultado = await handler.Handle(new ObtenerBlindajeEmpresasDeClienteQuery(ClienteId), CancellationToken.None);

        resultado.Should().ContainSingle(e => e.EmpresaId == empresa.Id);
    }
}
