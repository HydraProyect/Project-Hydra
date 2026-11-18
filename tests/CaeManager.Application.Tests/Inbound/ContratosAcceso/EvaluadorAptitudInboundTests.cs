using CaeManager.Application.Inbound.ContratosAcceso.AptosHoy;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Inbound.Requisitos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Inbound.ContratosAcceso;

/// <summary>
/// El servicio extraído de «Aptos hoy» con el filtro opcional por Trabajador (AF-05a): un Trabajador que existe en el Centro sale
/// con el mismo resultado que en la lista completa; uno que no existe, vacío; y un requisito de salud no filtra ni la conclusión
/// médica ni su fecha. Los datos son ficticios.
/// </summary>
public class EvaluadorAptitudInboundTests
{
    // Miércoles 17/11/2026, 11:00 en Madrid (10:00 UTC).
    private static readonly DateTimeOffset Ahora = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private readonly Guid _cliente = Guid.NewGuid();
    private readonly Guid _centro = Guid.NewGuid();
    private readonly Guid _tenantContratista = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly ContratosAccesoQueryContextFalso _contratos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly PlantillaRequisitosRepositorioFalso _plantillas = new();
    private readonly List<EvidenciaInbound> _evidencias = [];
    private readonly ContratoAcceso _contrato;
    private readonly TipoDocumento _formacion;
    private readonly TipoDocumento _aptitud;

    public EvaluadorAptitudInboundTests()
    {
        _contrato = ContratoAcceso.Crear(_cliente, _centro, Guid.NewGuid(), new DateOnly(2026, 1, 1), null, null);
        _contrato.VincularTenantContratista(_tenantContratista);
        _contratos.ListaContratos.Add(_contrato);

        _formacion = Tipo("Carné de operador de plataforma elevadora", AmbitoAplicacion.Trabajador,
            GrupoTipoDocumento.HabilitacionesYOficios, SensibilidadDocumental.DatosPersonales);
        _aptitud = Tipo("Certificado de aptitud", AmbitoAplicacion.Trabajador, GrupoTipoDocumento.VigilanciaDeLaSalud,
            SensibilidadDocumental.CategoriaEspecialSalud);
        _aptitud.EstablecerSoloConclusionAptitud(true);

        var plantilla = PlantillaRequisitos.ParaCentro(_centro);
        Requisito(plantilla, _formacion, ModoVigenciaRequisito.Fija, 12, tolerancia: 15);
        Requisito(plantilla, _aptitud, ModoVigenciaRequisito.Fija, 12);
        _plantillas.Plantillas.Add(plantilla);
    }

    [Fact]
    public async Task Con_filtro_un_Trabajador_que_existe_sale_solo_y_con_el_mismo_resultado_que_en_la_lista_completa()
    {
        var pilar = Guid.NewGuid();
        Dar(pilar, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2025, 11, 10)); // vencida hace 7 días: en tolerancia
        Dar(pilar, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), ConclusionAptitud.Apto);
        var ivan = Guid.NewGuid();
        Dar(ivan, "Iván Bescós Lacasa", _formacion, new DateOnly(2026, 6, 1)); // sin aptitud: no apto

        var completa = await Evaluar(null);
        var filtrada = await Evaluar(pilar);

        completa.Trabajadores.Should().HaveCount(2);
        var delFiltro = filtrada.Trabajadores.Should().ContainSingle().Subject;
        delFiltro.Dto.TrabajadorId.Should().Be(pilar);
        delFiltro.Dto.Estado.Should().Be(EstadoAptitudInbound.EnTolerancia);
        delFiltro.Dto.Should().BeEquivalentTo(completa.Trabajadores.Single(t => t.Dto.TrabajadorId == pilar).Dto);
        delFiltro.Dto.EmpresasContratistaIds.Should().Equal(_contrato.EmpresaContratistaId);
    }

    [Fact]
    public async Task Con_filtro_un_Trabajador_que_no_existe_en_el_Centro_da_vacio()
    {
        Dar(Guid.NewGuid(), "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));

        var evaluacion = await Evaluar(Guid.NewGuid());

        evaluacion.Trabajadores.Should().BeEmpty();
    }

    [Theory]
    [InlineData("vencido")]
    [InlineData("no apto transcrito")]
    public async Task Un_requisito_de_salud_que_no_se_acredita_sale_como_NoAcreditado_y_sin_fecha(string caso)
    {
        var pilar = Guid.NewGuid();
        Dar(pilar, "Pilar Sanjuán Aznar", _formacion, new DateOnly(2026, 6, 1));
        if (caso == "vencido")
            Dar(pilar, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2024, 1, 1), ConclusionAptitud.Apto);
        else
            Dar(pilar, "Pilar Sanjuán Aznar", _aptitud, new DateOnly(2026, 9, 1), ConclusionAptitud.NoApto);

        var fila = (await Evaluar(pilar)).Trabajadores.Should().ContainSingle().Subject.Dto;

        fila.Estado.Should().Be(EstadoAptitudInbound.NoApto);
        fila.Requisito.Should().Be(_aptitud.Nombre);
        fila.Causa.Should().Be(CausaNoAptitudInbound.NoAcreditado);
        fila.FechaVencimiento.Should().BeNull("la fecha delataría la conclusión médica (art. 9 RGPD)");
    }

    private async Task<EvaluacionAptitudCentro> Evaluar(Guid? trabajador)
    {
        using var reloj = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(Ahora));
        var evaluador = new EvaluadorAptitudInbound(
            _contratos, new EvidenciasQueryContextFalso(_evidencias), new PlantillasRequisitosQueryContextFalso(_plantillas), _tipos,
            new AlcanceDatosServiceFalso());
        return await evaluador.EvaluarAsync(_centro, trabajador, CancellationToken.None);
    }

    private TipoDocumento Tipo(string nombre, AmbitoAplicacion ambito, GrupoTipoDocumento? grupo, SensibilidadDocumental sensibilidad)
    {
        var tipo = new TipoDocumento(nombre, 12, aplicaVencimientoAutomatico: true, orden: 1, ambito, sensibilidad: sensibilidad);
        tipo.EstablecerGrupo(grupo);
        _tipos.ListaTiposDocumento.Add(tipo);
        return tipo;
    }

    private static RequisitoPlantilla Requisito(
        PlantillaRequisitos plantilla, TipoDocumento tipo, ModoVigenciaRequisito modo, int? meses, int? tolerancia = null) =>
        plantilla.AnadirRequisito(tipo, requisitoBase: null, new AtributosRequisito(
            OrigenRequisito.PropioTenant, modo, meses, tolerancia, true, null, DestinatarioRequisito.Todos, TiposContratacion.Todos, [],
            AmbitoDocumentoRequisito.GeneralTec));

    private void Dar(Guid trabajador, string nombre, TipoDocumento tipo, DateOnly emision, ConclusionAptitud? conclusion = null) =>
        _evidencias.Add(EvidenciaInbound.Registrar(
            new EvidenciaInbound.Procedencia(_contrato.Id, _tenantContratista, Guid.NewGuid(), Guid.NewGuid()),
            tipo.Id,
            new EvidenciaInbound.Sujeto(trabajador, nombre, "12345678Z"),
            emision, null, soloConclusionAptitud: conclusion is not null, conclusion,
            conclusion is null ? new EvidenciaInbound.ArchivoCopiado($"{Guid.NewGuid():N}/copia.pdf", new string('a', 64)) : null,
            _usuario, Ahora.UtcDateTime));

    private sealed class EvidenciasQueryContextFalso(List<EvidenciaInbound> evidencias) : IEvidenciasInboundQueryContext
    {
        public IQueryable<EvidenciaInbound> EvidenciasInbound => new TestAsyncQueryable<EvidenciaInbound>(evidencias.AsQueryable());
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }
}
