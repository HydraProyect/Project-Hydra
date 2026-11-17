using Bunit;
using CaeManager.Application.Inbound.AutorizacionesLectura.Queries.ObtenerRequerimientosTec;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Inbound.TenantTec.Pages;
using CaeManager.Web.Features.Inbound.TenantTec.Recursos;
using CaeManager.Web.Features.Inbound.TenantTec.Servicios;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El adaptador de Requerimientos del Tenant TEC (demo Inbound, P1): traduce lo que calcula
/// <see cref="ObtenerRequerimientosTecQuery"/> a las filas de la pantalla, sin veredictos (P0b): solo «Pendiente de subir» o
/// «Pendiente de validar», sin motivo, sin historial y sin lectura automática. Datos ficticios.
/// </summary>
public sealed class RequerimientosTenantTecConBackendTests : BunitContext
{
    private static readonly Guid ContratoId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    public RequerimientosTenantTecConBackendTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
    }

    private sealed class MediadorDePrueba(IReadOnlyList<RequerimientoTecDto> filas, Exception? fallo = null) : IMediator
    {
        public int Consultas { get; private set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Consultas++;
            return request is ObtenerRequerimientosTecQuery
                ? fallo is null ? (TResponse)(object)filas : throw fallo
                : throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private static RequerimientoTecDto Fila(
        string tipo, SujetoRequerimientoTec sujeto, string sujetoNombre, bool conDocumento,
        string cliente = "Logística Delta Sur S.A.", EstadoDocumento? estadoDocumento = null, DateOnly? vence = null,
        string capa = "DeCentro") =>
        new(Guid.NewGuid(), ContratoId, Guid.NewGuid(), sujeto, Guid.NewGuid(), "Logística Delta Sur", cliente, "Planta Zaragoza Norte",
            tipo, "Certificado vigente", capa, true, "Montajes Moncayo S.L.", sujetoNombre,
            conDocumento ? EstadoRequerimientoTecSinVeredicto.PendienteDeValidar : EstadoRequerimientoTecSinVeredicto.PendienteDeSubir,
            conDocumento ? estadoDocumento ?? EstadoDocumento.Vigente : null, vence, conDocumento ? 1 : 0, false);

    private RequerimientosTenantTecConBackend Adaptador(MediadorDePrueba mediador) =>
        new(mediador, Services.GetRequiredService<IStringLocalizer<TextosInboundTenantTec>>());

    [Fact]
    public async Task Sin_documento_es_Pendiente_de_subir_con_la_accion_Subir()
    {
        var dto = Fila("Formación en altura", SujetoRequerimientoTec.Trabajador, "Luis Gómez Ara", conDocumento: false);

        var r = (await Adaptador(new MediadorDePrueba([dto])).ListarAsync()).Should().ContainSingle().Subject;

        r.Id.Should().Be(dto.Id);
        r.ClienteEmpresarial.Should().Be("Logística Delta Sur S.A.");
        r.Nombre.Should().Be("Formación en altura");
        r.Empresa.Should().Be("Montajes Moncayo S.L.");
        r.Entidad.Should().Be(EntidadRequerimientoTec.Trabajador);
        r.ReferenciaEntidad.Should().Be("Luis Gómez Ara");
        r.Centro.Should().Be("Planta Zaragoza Norte");
        r.Estado.Should().Be(EstadoRequerimientoTec.PendienteDeSubir);
        r.Vigencia.Should().Be(VigenciaRequerimientoTec.SinVigencia);
        r.Accion.Should().Be(AccionRequerimientoTec.Subir);
        r.HayDocumento.Should().BeFalse();
        r.Criterio.Should().Be("Certificado vigente");
        r.Motivo.Should().BeNull();
        r.Historial.Should().BeEmpty();
        r.Lectura.Should().BeEmpty();
        r.Niveles.Should().ContainSingle().Which.Nivel.Should().Be("Centro");
    }

    [Theory]
    [InlineData(EstadoDocumento.Vigente, VigenciaRequerimientoTec.Vigente)]
    [InlineData(EstadoDocumento.SinCaducidad, VigenciaRequerimientoTec.Vigente)]
    [InlineData(EstadoDocumento.Proximo, VigenciaRequerimientoTec.Proximo)]
    [InlineData(EstadoDocumento.Urgente, VigenciaRequerimientoTec.Urgente)]
    [InlineData(EstadoDocumento.SinConfirmar, VigenciaRequerimientoTec.SinVigencia)]
    public async Task Con_documento_vigente_propio_es_Pendiente_de_validar_sin_accion_y_con_su_vigencia(
        EstadoDocumento estado, VigenciaRequerimientoTec vigencia)
    {
        var vence = new DateOnly(2027, 3, 4);
        var dto = Fila("Seguro de responsabilidad civil", SujetoRequerimientoTec.Empresa, "Montajes Moncayo S.L.", conDocumento: true,
            estadoDocumento: estado, vence: vence, capa: "ExigenciasClienteEmpresarial");

        var r = (await Adaptador(new MediadorDePrueba([dto])).ListarAsync()).Should().ContainSingle().Subject;

        r.Estado.Should().Be(EstadoRequerimientoTec.PendienteDeValidar);
        r.Entidad.Should().Be(EntidadRequerimientoTec.Empresa);
        r.Vigencia.Should().Be(vigencia);
        r.Hasta.Should().Be(vence);
        r.Vigencias.Should().Be(1);
        r.Accion.Should().Be(AccionRequerimientoTec.Ninguna);
        r.HayDocumento.Should().BeTrue();
        r.Niveles.Should().ContainSingle().Which.Nivel.Should().Be("Exigencias del Cliente");
    }

    [Fact]
    public async Task Esta_disponible_y_un_fallo_de_la_lectura_sube_a_la_pantalla()
    {
        var adaptador = Adaptador(new MediadorDePrueba([], new InvalidOperationException("42501")));

        adaptador.Disponible.Should().BeTrue();
        await adaptador.Invoking(a => a.ListarAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void La_pantalla_con_el_adaptador_real_pinta_pestanas_por_Cliente_empresarial_estados_sin_veredicto_y_Subir_solo_sin_documento()
    {
        var mediador = new MediadorDePrueba(
        [
            Fila("Formación en altura", SujetoRequerimientoTec.Trabajador, "Luis Gómez Ara", conDocumento: false),
            Fila("Seguro de responsabilidad civil", SujetoRequerimientoTec.Empresa, "Montajes Moncayo S.L.", conDocumento: true),
            Fila("Alta en la Seguridad Social", SujetoRequerimientoTec.Trabajador, "Luis Gómez Ara", conDocumento: false,
                cliente: "Frío Industrial Cierzo S.L."),
        ]);
        Services.AddSingleton<IMediator>(mediador);
        Services.AddScoped<IRequerimientosTenantTec, RequerimientosTenantTecConBackend>();
        Services.AddScoped<ISubidaDocumentoTenantTec, SubidaDocumentoTenantTecSinBackend>();

        var cut = Render<Requerimientos>();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        mediador.Consultas.Should().Be(1);
        cut.Markup.Should().NotContain("Los requerimientos todavía no están disponibles");
        cut.FindAll("[role=tab]").Select(t => t.TextContent).Should().HaveCount(2)
            .And.Contain(t => t.Contains("Logística Delta Sur S.A."))
            .And.Contain(t => t.Contains("Frío Industrial Cierzo S.L."));

        var filas = cut.FindAll("tbody tr");
        var pendiente = filas.Single(f => f.TextContent.Contains("Formación en altura"));
        pendiente.TextContent.Should().Contain("Pendiente de subir").And.Contain("Luis Gómez Ara").And.Contain("Planta Zaragoza Norte");
        pendiente.QuerySelectorAll("button").Should().Contain(b => b.TextContent.Trim() == "Subir");
        var subido = filas.Single(f => f.TextContent.Contains("Seguro de responsabilidad civil"));
        subido.TextContent.Should().Contain("Pendiente de validar");
        subido.QuerySelectorAll("button").Should().NotContain(b => b.TextContent.Trim() == "Subir");
        cut.FindAll("tbody").Single().TextContent.Should().NotContain("Validado").And.NotContain("Rechazado").And.NotContain("Motivo");
    }

    [Fact]
    public void La_pantalla_sin_requerimientos_dice_que_no_tienes_ninguno()
    {
        Services.AddSingleton<IMediator>(new MediadorDePrueba([]));
        Services.AddScoped<IRequerimientosTenantTec, RequerimientosTenantTecConBackend>();
        Services.AddScoped<ISubidaDocumentoTenantTec, SubidaDocumentoTenantTecSinBackend>();

        var cut = Render<Requerimientos>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No tienes requerimientos"));
        cut.FindAll("table").Should().BeEmpty();
    }
}
