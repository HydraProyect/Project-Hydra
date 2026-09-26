using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Estado de un Tenant en memoria para probar la regla de alta de
/// acreditaciones de plataforma (P0-7) y los caminos que la invocan: los
/// contextos de consulta falsos son "lo que ya está guardado" y
/// <see cref="Acreditaciones"/> recoge lo que el servicio agrega sin guardar.
/// El servicio es el REAL (<see cref="AltaAcreditacionesPlataformaService"/>),
/// no un doble: cada camino se prueba contra la regla de verdad.
/// </summary>
internal sealed class MundoAcreditaciones
{
    private static readonly DateOnly Hoy = new(2026, 1, 15);

    public AsignacionesQueryContextFalso AsignacionesContexto { get; } = new();
    public CentrosQueryContextFalso CentrosContexto { get; } = new();
    public TrabajadoresQueryContextFalso TrabajadoresContexto { get; } = new();
    public DocumentosQueryContextFalso DocumentosContexto { get; } = new();
    public TiposDocumentoQueryContextFalso TiposDocumentoContexto { get; } = new();
    public AcreditacionDocumentoPlataformaRepositorioFalso Acreditaciones { get; } = new();

    public Guid EmpresaId { get; } = Guid.NewGuid();

    public AltaAcreditacionesPlataformaService Servicio() =>
        new(AsignacionesContexto, CentrosContexto, TrabajadoresContexto, DocumentosContexto, TiposDocumentoContexto, Acreditaciones);

    public Trabajador Trabajador(string dni = "12345678Z")
    {
        var trabajador = Domain.Trabajadores.Trabajador.DeEmpresa(EmpresaId, "Ana", "Pérez", dni);
        TrabajadoresContexto.ListaTrabajadores.Add(trabajador);
        return trabajador;
    }

    public Centro Centro(string nombre = "Planta Norte")
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), nombre);
        CentrosContexto.ListaCentros.Add(centro);
        return centro;
    }

    public CanalGestionDocumental AccesoPlataforma(Centro centro)
    {
        var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Gestión general", Guid.NewGuid(), null, null, null);
        CentrosContexto.ListaCanalesGestionDocumental.Add(canal);
        return canal;
    }

    public CanalGestionDocumental AccesoCorreo(Centro centro)
    {
        var canal = CanalGestionDocumental.PorEmail(centro.Id, "Correo CAE", "cae@contratista.es", null);
        CentrosContexto.ListaCanalesGestionDocumental.Add(canal);
        return canal;
    }

    public TipoDocumento Tipo(
        RequisitoDocumental requerido, AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador, string nombre = "Formación PRL")
    {
        var tipo = new TipoDocumento(nombre, 12, aplicaVencimientoAutomatico: true, orden: 1, ambito, requerido);
        TiposDocumentoContexto.ListaTiposDocumento.Add(tipo);
        return tipo;
    }

    /// <summary>Posición explícita (guardada) de un Centro sobre un tipo.</summary>
    public TipoDocumentoCentro Requisito(TipoDocumento tipo, Centro centro, bool incluido)
    {
        var fila = new TipoDocumentoCentro(tipo.Id, centro.Id, incluido);
        TiposDocumentoContexto.ListaTiposDocumentoCentros.Add(fila);
        return fila;
    }

    public Asignacion Asignacion(Trabajador trabajador, Centro centro)
    {
        var asignacion = new Asignacion(trabajador.Id, centro.Id, Hoy);
        AsignacionesContexto.ListaAsignaciones.Add(asignacion);
        return asignacion;
    }

    public Documento DocumentoDe(Trabajador trabajador, TipoDocumento tipo)
    {
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(5)));
        DocumentosContexto.ListaDocumentos.Add(documento);
        return documento;
    }

    public Documento DocumentoDeEmpresa(TipoDocumento tipo)
    {
        var documento = Documento.DeEmpresa(EmpresaId, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(5)));
        DocumentosContexto.ListaDocumentos.Add(documento);
        return documento;
    }

    /// <summary>Simula el SaveChanges: lo agregado pasa a ser "lo que ya está en la base".</summary>
    public void Guardar()
    {
        DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.AddRange(Acreditaciones.Acreditaciones);
        Acreditaciones.Acreditaciones.Clear();
    }

    public IEnumerable<(Guid DocumentoId, Guid CanalId)> Agregadas =>
        Acreditaciones.Acreditaciones.Select(a => (a.DocumentoId, a.CanalGestionDocumentalId));
}
