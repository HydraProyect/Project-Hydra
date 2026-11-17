using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Inbound.ContratosAcceso.AptosHoy;
using CaeManager.Application.Inbound.ContratosAcceso.Queries;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratosAcceso;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Web.Features.Inbound.CentroInbound;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Recursos;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Tests;

/// <summary>
/// El adaptador de «Aptos hoy» (demo Inbound, pieza P3): traduce lo que calcula <see cref="ObtenerAptosHoyDelCentroQuery"/> a las
/// filas de la pantalla. Comprueba el DNI enmascarado, el nombre de la Empresa contratista, el detalle sin motivo clínico y que
/// una lectura que falla se enseña como «no disponible» (null) y no como una lista vacía. Datos ficticios.
/// </summary>
public class AptosHoyDelCentroConBackendTests
{
    private static readonly Guid CentroId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid ClienteId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid OtraEmpresaId = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    private sealed class MediadorDePrueba : IMediator
    {
        public IReadOnlyList<AptoHoyDto> Filas { get; init; } = [];
        public IReadOnlyList<ContratoAccesoDto> Contratos { get; init; } = [];
        public bool EmpresasConocidas { get; init; } = true;
        public Exception? Fallo { get; init; }
        public List<object> Peticiones { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Peticiones.Add(request);
            return (TResponse)(request switch
            {
                ObtenerAptosHoyDelCentroQuery => Fallo is null ? (object)Filas : throw Fallo,
                ObtenerContratosAccesoQuery => (object)Contratos,
                ObtenerCentrosParaSelectorQuery => (object)new[] { new CentroSelectorDto(CentroId, "Planta Zaragoza Norte", "Logística Delta Sur S.A.", "Logística Delta Sur S.A.", ClienteId) },
                ObtenerEmpresaPorIdQuery q => !EmpresasConocidas ? null! : (object)new EmpresaDetalleDto(
                    q.Id, q.Id == EmpresaId ? "Montajes Moncayo S.L." : "Instalaciones Cinca S.L.", "B12345674", DateTime.UtcNow, [], Guid.NewGuid()),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}."),
            });
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private static ContratoAccesoDto Contrato(Guid empresaId) =>
        new(Guid.NewGuid(), ClienteId, CentroId, empresaId, new DateOnly(2020, 1, 1), new DateOnly(2099, 12, 31), null, true, true, null,
            Guid.NewGuid(), EstadoContratoAcceso.Activo, TipoContratoAcceso.Recurrente, PropiaActividadContrato.Si, 1, 10, null, null);

    private static AptoHoyDto Fila(
        string nombre, EstadoAptitudInbound estado, string? requisito = null, SujetoDelRequisitoInbound? sujeto = null,
        CausaNoAptitudInbound? causa = null, DateOnly? vence = null, Guid? empresa = null, string? dni = "12345678Z",
        TipoContratoAcceso? tipo = TipoContratoAcceso.Recurrente) =>
        new(Guid.NewGuid(), nombre, dni, Guid.NewGuid(), empresa ?? EmpresaId, tipo, estado, requisito, sujeto, causa, vence);

    private static AptosHoyDelCentroConBackend Adaptador(MediadorDePrueba mediador)
    {
        var servicios = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        return new AptosHoyDelCentroConBackend(
            mediador, new ResolutorContratosAcceso(mediador), servicios.GetRequiredService<IStringLocalizer<TextosContratosAcceso>>());
    }

    [Fact]
    public async Task Traduce_cada_estado_con_la_Empresa_contratista_el_DNI_enmascarado_y_el_requisito()
    {
        var mediador = new MediadorDePrueba
        {
            Contratos = [Contrato(EmpresaId)],
            Filas =
            [
                Fila("Pilar Sanjuán Aznar", EstadoAptitudInbound.Apto, vence: new DateOnly(2027, 3, 2)),
                Fila("Iván Bescós Lacasa", EstadoAptitudInbound.EnTolerancia, "Carné de operador de plataforma elevadora",
                    SujetoDelRequisitoInbound.Trabajador, vence: new DateOnly(2026, 11, 10)),
                Fila("Javier Lasheras Pueyo", EstadoAptitudInbound.NoApto, "Certificado de aptitud", SujetoDelRequisitoInbound.Trabajador,
                    CausaNoAptitudInbound.SinDocumentoValidado),
            ],
        };

        var aptos = (await Adaptador(mediador).ObtenerAsync(CentroId))!;

        aptos.Should().NotBeNull();
        aptos.Should().HaveCount(3);
        aptos.Should().OnlyContain(a => a.EmpresaContratista == "Montajes Moncayo S.L." && a.TipoDeContrato == "Recurrente");
        aptos.Should().OnlyContain(a => a.DniParcial == "****678Z", "el DNI sale siempre enmascarado");

        var apto = aptos.Single(a => a.Estado == EstadoAptitudHoy.Apto);
        apto.RequisitoQueBloquea.Should().BeNull();
        apto.Detalle.Should().Be("Siguiente vencimiento: 02/03/2027");

        var tolerancia = aptos.Single(a => a.Estado == EstadoAptitudHoy.EnTolerancia);
        tolerancia.RequisitoQueBloquea.Should().Be("Carné de operador de plataforma elevadora");
        tolerancia.Detalle.Should().Be("Vencido el 10/11/2026, en tolerancia");

        var noApto = aptos.Single(a => a.Estado == EstadoAptitudHoy.NoApto);
        noApto.RequisitoQueBloquea.Should().Be("Certificado de aptitud");
        noApto.Detalle.Should().Be("Sin documento validado");
    }

    [Theory]
    [InlineData(CausaNoAptitudInbound.Vencido, "2026-10-30", "Vencido el 30/10/2026")]
    [InlineData(CausaNoAptitudInbound.SinFechaDeVencimiento, null, "El documento no trae fecha de vencimiento")]
    [InlineData(CausaNoAptitudInbound.NoAcreditado, null, "Vencido")]
    [InlineData(CausaNoAptitudInbound.SinDocumentoValidado, null, "Sin documento validado")]
    public async Task El_detalle_de_un_No_apto_dice_la_causa_sin_ningun_motivo_clinico(CausaNoAptitudInbound causa, string? vence, string esperado)
    {
        var mediador = new MediadorDePrueba
        {
            Contratos = [Contrato(EmpresaId)],
            Filas =
            [
                Fila("Javier Lasheras Pueyo", EstadoAptitudInbound.NoApto, "Certificado de aptitud", SujetoDelRequisitoInbound.Trabajador,
                    causa, vence is null ? null : DateOnly.Parse(vence)),
            ],
        };

        var aptos = (await Adaptador(mediador).ObtenerAsync(CentroId))!;

        aptos.Single().Detalle.Should().Be(esperado);
        aptos.Single().RequisitoQueBloquea.Should().Be("Certificado de aptitud");
    }

    [Fact]
    public async Task Un_requisito_que_revela_salud_dice_solo_Vencido_y_lo_mismo_que_uno_vencido_sin_fecha()
    {
        var mediador = new MediadorDePrueba
        {
            Contratos = [Contrato(EmpresaId)],
            Filas =
            [
                Fila("Javier Lasheras Pueyo", EstadoAptitudInbound.NoApto, "Certificado de aptitud", SujetoDelRequisitoInbound.Trabajador,
                    CausaNoAptitudInbound.NoAcreditado),
                Fila("Pilar Sanjuán Aznar", EstadoAptitudInbound.NoApto, "Certificado de aptitud", SujetoDelRequisitoInbound.Trabajador,
                    CausaNoAptitudInbound.Vencido),
            ],
        };

        var aptos = (await Adaptador(mediador).ObtenerAsync(CentroId))!;

        aptos.Select(a => a.Detalle).Distinct().Should().Equal("Vencido");
    }

    [Fact]
    public async Task La_proyeccion_restringida_no_inventa_requisito_ni_detalle()
    {
        var mediador = new MediadorDePrueba
        {
            Contratos = [Contrato(EmpresaId)],
            Filas =
            [
                Fila("Javier Lasheras Pueyo", EstadoAptitudInbound.NoApto, dni: "****678Z", tipo: null) with { ProyeccionCompleta = false },
                Fila("Pilar Sanjuán Aznar", EstadoAptitudInbound.Apto, dni: "****678Z", tipo: null) with { ProyeccionCompleta = false },
            ],
        };

        var aptos = (await Adaptador(mediador).ObtenerAsync(CentroId))!;

        aptos.Should().OnlyContain(a => a.RequisitoQueBloquea == null && a.Detalle == null && !a.ConRequisito);
        aptos.Should().OnlyContain(a => a.DniParcial == "****678Z" && a.EmpresaContratista == "Montajes Moncayo S.L.");
        aptos.Select(a => a.Estado).Should().Equal(EstadoAptitudHoy.NoApto, EstadoAptitudHoy.Apto);
    }

    [Fact]
    public async Task Un_requisito_de_la_Empresa_contratista_lo_dice_porque_deja_fuera_a_todos_sus_Trabajadores()
    {
        var mediador = new MediadorDePrueba
        {
            Contratos = [Contrato(EmpresaId)],
            Filas =
            [
                Fila("Pilar Sanjuán Aznar", EstadoAptitudInbound.NoApto, "Seguro de responsabilidad civil",
                    SujetoDelRequisitoInbound.EmpresaContratista, CausaNoAptitudInbound.SinDocumentoValidado),
            ],
        };

        var aptos = (await Adaptador(mediador).ObtenerAsync(CentroId))!;

        aptos.Single().RequisitoQueBloquea.Should().Be("Seguro de responsabilidad civil");
        aptos.Single().Detalle.Should().Be("Requisito de la Empresa contratista · Sin documento validado");
    }

    [Fact]
    public async Task Cada_Trabajador_lleva_el_nombre_de_su_Empresa_contratista()
    {
        var mediador = new MediadorDePrueba
        {
            Contratos = [Contrato(EmpresaId), Contrato(OtraEmpresaId)],
            Filas =
            [
                Fila("Pilar Sanjuán Aznar", EstadoAptitudInbound.Apto, empresa: EmpresaId),
                Fila("Iván Bescós Lacasa", EstadoAptitudInbound.Apto, empresa: OtraEmpresaId),
            ],
        };

        var aptos = (await Adaptador(mediador).ObtenerAsync(CentroId))!;

        aptos.Single(a => a.Nombre == "Pilar Sanjuán Aznar").EmpresaContratista.Should().Be("Montajes Moncayo S.L.");
        aptos.Single(a => a.Nombre == "Iván Bescós Lacasa").EmpresaContratista.Should().Be("Instalaciones Cinca S.L.");
    }

    [Fact]
    public async Task Solo_se_pide_el_nombre_de_las_Empresas_que_salen_en_alguna_fila()
    {
        var mediador = new MediadorDePrueba
        {
            Contratos = [Contrato(EmpresaId), Contrato(OtraEmpresaId)],
            Filas = [Fila("Pilar Sanjuán Aznar", EstadoAptitudInbound.Apto, empresa: EmpresaId)],
        };

        await Adaptador(mediador).ObtenerAsync(CentroId);

        mediador.Peticiones.OfType<ObtenerEmpresaPorIdQuery>().Select(q => q.Id).Should().Equal(EmpresaId);
    }

    [Fact]
    public async Task Una_Empresa_que_la_lectura_no_devuelve_se_dice_sin_inventar_su_nombre()
    {
        var mediador = new MediadorDePrueba
        {
            Contratos = [Contrato(EmpresaId)],
            EmpresasConocidas = false,
            Filas = [Fila("Pilar Sanjuán Aznar", EstadoAptitudInbound.Apto)],
        };

        var aptos = (await Adaptador(mediador).ObtenerAsync(CentroId))!;

        aptos.Single().EmpresaContratista.Should().Be("Empresa no disponible");
    }

    [Fact]
    public async Task Un_contrato_sin_tipo_enseña_el_guion_de_sin_dato()
    {
        var mediador = new MediadorDePrueba
        {
            Contratos = [Contrato(EmpresaId)],
            Filas = [Fila("Pilar Sanjuán Aznar", EstadoAptitudInbound.Apto, tipo: null)],
        };

        (await Adaptador(mediador).ObtenerAsync(CentroId))!.Single().TipoDeContrato.Should().Be("—");
    }

    [Fact]
    public async Task Sin_filas_devuelve_una_lista_vacia_y_no_lee_contratos_ni_Empresas()
    {
        var mediador = new MediadorDePrueba();

        var aptos = (await Adaptador(mediador).ObtenerAsync(CentroId))!;

        aptos.Should().NotBeNull().And.BeEmpty();
        mediador.Peticiones.Should().ContainSingle().Which.Should().Be(new ObtenerAptosHoyDelCentroQuery(CentroId));
    }

    [Fact]
    public async Task Si_la_lectura_falla_devuelve_null_para_que_la_pantalla_diga_no_disponible()
    {
        var mediador = new MediadorDePrueba { Fallo = new InvalidOperationException("La lectura falló.") };

        (await Adaptador(mediador).ObtenerAsync(CentroId)).Should().BeNull("una lista vacía diría «nadie asignado», que no es lo mismo");
    }

    [Fact]
    public async Task Una_cancelacion_no_se_confunde_con_un_fallo_de_lectura()
    {
        using var origen = new CancellationTokenSource();
        await origen.CancelAsync();
        var mediador = new MediadorDePrueba { Fallo = new OperationCanceledException(origen.Token) };

        var accion = () => Adaptador(mediador).ObtenerAsync(CentroId, origen.Token);

        await accion.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void El_modelo_que_recibe_la_pantalla_no_tiene_propiedad_de_motivo_ni_de_contenido_clinico()
    {
        var propiedades = typeof(TrabajadorAptoHoy).GetProperties().Select(p => p.Name)
            .Concat(typeof(AptoHoyDto).GetProperties().Select(p => p.Name)).ToList();

        propiedades.Should().NotContain(n =>
            n.Contains("Motivo", StringComparison.OrdinalIgnoreCase) || n.Contains("Conclusion", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Medic", StringComparison.OrdinalIgnoreCase) || n.Contains("Salud", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Archivo", StringComparison.OrdinalIgnoreCase) || n.Contains("Clinic", StringComparison.OrdinalIgnoreCase));
    }
}
