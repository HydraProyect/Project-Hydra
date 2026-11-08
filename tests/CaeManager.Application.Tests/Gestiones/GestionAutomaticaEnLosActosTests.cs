using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Gestiones;

/// <summary>
/// Qué actos hacen nacer la Gestión automática y que nace en el mismo guardado que el acto (hallazgos 1, 3, 4 y 7 de la
/// primera ronda de Codex sobre la PR #2072). El guardado falso fotografía las Gestiones en el momento del único
/// <c>SaveChangesAsync</c>: lo que no esté ahí no se confirmó con el Documento.
/// </summary>
public class GestionAutomaticaEnLosActosTests
{
    private static readonly DateOnly Hoy = new(2026, 1, 15);

    private readonly MundoAcreditaciones _mundo = new();
    private readonly DocumentoRepositorioFalso _documentos = new();
    private readonly GuardadoQueFotografia _guardado;

    public GestionAutomaticaEnLosActosTests() => _guardado = new GuardadoQueFotografia(_mundo);

    [Fact]
    public async Task Dar_de_alta_con_archivo_hace_nacer_la_gestion_en_el_mismo_guardado_que_el_documento()
    {
        var (trabajador, tipo, acceso) = TrabajadorEnUnCentroConPlataforma();

        var resultado = await Crear().Handle(NuevoCrear(trabajador.Id, tipo.Id, "tenant/apto.pdf"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var foto = _guardado.Fotos.Should().ContainSingle("el alta, sus acreditaciones y la Gestión se guardan juntas").Subject;
        var gestion = foto.Should().ContainSingle().Subject;
        gestion.DocumentoId.Should().Be(resultado.Valor);
        gestion.Origen.Should().Be(OrigenGestionAutomatica.AltaDocumento);
        gestion.Pasos.Should().ContainSingle();
        _mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma
            .Single(a => a.Id == gestion.Pasos.Single().AcreditacionId).CanalGestionDocumentalId.Should().Be(acceso.Id);
    }

    [Fact]
    public async Task Dar_de_alta_sin_archivo_acredita_pero_no_hace_nacer_gestion()
    {
        var (trabajador, tipo, _) = TrabajadorEnUnCentroConPlataforma();

        var resultado = await Crear().Handle(NuevoCrear(trabajador.Id, tipo.Id, null), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Should().ContainSingle("control: el alta sí acredita");
        _mundo.Gestiones.Lista.Should().BeEmpty();
    }

    [Fact]
    public async Task Dar_de_alta_con_archivo_sin_accesos_de_plataforma_no_hace_nacer_gestion()
    {
        // Hallazgo 7, rechazado por decisión: sin accesos de plataforma no hay nada que gestionar.
        var trabajador = _mundo.Trabajador();
        var tipo = _mundo.Tipo(RequisitoDocumental.Si);
        var centro = _mundo.Centro("Planta Norte");
        _mundo.AccesoCorreo(centro);
        _mundo.Asignacion(trabajador, centro);

        var resultado = await Crear().Handle(NuevoCrear(trabajador.Id, tipo.Id, "tenant/apto.pdf"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _guardado.Fotos.Should().ContainSingle();
        _mundo.Gestiones.Lista.Should().BeEmpty();
    }

    [Fact]
    public async Task Renovar_con_archivo_hace_nacer_la_nueva_y_sustituye_la_anterior_en_el_mismo_guardado()
    {
        var (trabajador, tipo, acceso) = TrabajadorEnUnCentroConPlataforma();
        var (anterior, gestionAnterior) = DocumentoConSuGestionAbierta(trabajador, tipo, acceso);

        var resultado = await Renovar().Handle(NuevoRenovar(anterior.Id, "tenant/apto-renovado.pdf"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        // La Gestión nueva se busca en la fotografía del único guardado, no en la lista que sigue cambiando después:
        // una agregada tras el SaveChanges no se habría confirmado con el Documento (BAJO de la ronda 3 de Codex).
        var foto = _guardado.Fotos.Should().ContainSingle().Subject;
        var gestionNueva = foto.Should().ContainSingle(g => g.DocumentoId == resultado.Valor,
            "la Gestión nueva se guarda con el Documento renovado").Subject;
        gestionNueva.Origen.Should().Be(OrigenGestionAutomatica.RenovacionDocumento);
        _guardado.SustituidasAlGuardar.Should().Equal(gestionAnterior.Id);
        gestionAnterior.SustituidaPorGestionId.Should().Be(gestionNueva.Id);
    }

    [Fact]
    public async Task Corregir_fechas_sin_archivo_nuevo_no_hace_nacer_gestion_ni_toca_la_anterior()
    {
        var (trabajador, tipo, acceso) = TrabajadorEnUnCentroConPlataforma();
        var (anterior, gestionAnterior) = DocumentoConSuGestionAbierta(trabajador, tipo, acceso);

        var resultado = await Renovar().Handle(NuevoRenovar(anterior.Id, anterior.ArchivoUrl), CancellationToken.None);

        resultado.Valor.Should().Be(anterior.Id, "control: es una corrección de fechas, el mismo registro");
        _mundo.Gestiones.Lista.Should().Equal(gestionAnterior);
        gestionAnterior.EstaSustituida.Should().BeFalse();
    }

    // ---------- Montaje ----------

    private (Domain.Trabajadores.Trabajador Trabajador, TipoDocumento Tipo, Domain.Centros.CanalGestionDocumental Acceso)
        TrabajadorEnUnCentroConPlataforma()
    {
        var trabajador = _mundo.Trabajador();
        var tipo = _mundo.Tipo(RequisitoDocumental.Si);
        var centro = _mundo.Centro("Planta Norte");
        var acceso = _mundo.AccesoPlataforma(centro);
        _mundo.Asignacion(trabajador, centro);
        return (trabajador, tipo, acceso);
    }

    /// <summary>Un Documento con archivo ya guardado, con su acreditación pendiente y su Gestión abierta.</summary>
    private (Documento Documento, GestionAutomatica Gestion) DocumentoConSuGestionAbierta(
        Domain.Trabajadores.Trabajador trabajador, TipoDocumento tipo, Domain.Centros.CanalGestionDocumental acceso)
    {
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1)), "tenant/apto.pdf");
        _mundo.DocumentosContexto.ListaDocumentos.Add(documento);
        _documentos.Documentos.Add(documento);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, acceso.Id);
        _mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
        var gestion = GestionAutomatica.Nacer(documento.Id, OrigenGestionAutomatica.AltaDocumento, [acreditacion.Id], null)!;
        _mundo.Gestiones.Agregar(gestion);
        return (documento, gestion);
    }

    private CrearDocumentoCommandHandler Crear() => new(
        _documentos, _mundo.TiposDocumentoContexto, _mundo.TrabajadoresContexto, new VehiculosVacios(),
        new ProyectosQueryContextFalso(), new EmpresasQueryContextFalso(), _guardado,
        new ColaQueAcepta(), new CurrentUserServiceFalso(), _mundo.Servicio(), new PublisherFalso(),
        new AlcanceDatosServiceFalso(), new VerificadorArchivoAlmacenadoPermisivo(), _mundo.GestionAutomatica());

    private RenovarDocumentoCommandHandler Renovar() => new(
        _documentos, _mundo.TiposDocumentoContexto, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
        new ColaQueAcepta(), new CurrentUserServiceFalso(), _mundo.Acreditaciones, _mundo.Servicio(), new PublisherFalso(),
        _guardado, new VerificadorArchivoAlmacenadoPermisivo(), _mundo.GestionAutomatica());

    private static CrearDocumentoCommand NuevoCrear(Guid trabajadorId, Guid tipoId, string? archivo) =>
        new(trabajadorId, null, null, null, null, tipoId, Hoy, null, archivo, null);

    private static RenovarDocumentoCommand NuevoRenovar(Guid documentoId, string? archivo) =>
        new(documentoId, Hoy, null, archivo, null, NoCaduca: true);

    /// <summary>
    /// El único guardado del acto: fotografía las Gestiones y las sustituidas en ese momento y, como el SaveChanges, pasa
    /// lo agregado a «lo que ya está en la base».
    /// </summary>
    private sealed class GuardadoQueFotografia(MundoAcreditaciones mundo) : IUnitOfWork
    {
        public List<IReadOnlyList<GestionAutomatica>> Fotos { get; } = [];
        public List<Guid> SustituidasAlGuardar { get; } = [];

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Fotos.Add(mundo.Gestiones.Lista.ToList());
            SustituidasAlGuardar.Clear();
            SustituidasAlGuardar.AddRange(mundo.Gestiones.Lista.Where(g => g.EstaSustituida).Select(g => g.Id));
            mundo.Guardar();
            return Task.FromResult(1);
        }
    }

    private sealed class VehiculosVacios : IVehiculosQueryContext
    {
        public IQueryable<Vehiculo> Vehiculos => new Integraciones.TestAsyncQueryable<Vehiculo>(Array.Empty<Vehiculo>().AsQueryable());
    }

    private sealed class ColaQueAcepta : ITrabajoAnalisisDocumentoRepository
    {
        public void Agregar(TrabajoAnalisisDocumento trabajo) { }

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(TimeSpan umbral, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
