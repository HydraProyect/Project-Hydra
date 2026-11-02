using CaeManager.Domain.Documentos;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>De quién es un Documento resultante de Dividir: el «Es de» de la pantalla.</summary>
public enum ClaseTitular
{
    EmpresaGestionada = 0,
    Subcontrata = 1,
    Trabajador = 2,
    Vehiculo = 3
}

/// <summary>Por qué la detección propone lo que propone. Va como clave para que la pantalla lo traduzca.</summary>
public enum MotivoDeteccion
{
    SinTexto = 0,
    SinCoincidencias = 1,
    TipoSinTitular = 2,
    VariosTitulares = 3,
    CifEmpresaGestionada = 4,
    CifSubcontrata = 5,
    Dni = 6,
    Nombre = 7,
    Matricula = 8
}

public record EmpresaDivisionPdf(Guid Id, string RazonSocial, string? Cif);

/// <param name="EmpresaDuenaId">La Empresa gestionada o Subcontrata a la que pertenece el Trabajador.</param>
public record TrabajadorDivisionPdf(Guid Id, string Nombre, string Apellidos, string? Dni, Guid EmpresaDuenaId)
{
    public string NombreCompleto => $"{Nombre} {Apellidos}".Trim();
}

/// <param name="EmpresaDuenaId">La Empresa gestionada o Subcontrata a la que pertenece el Vehículo.</param>
public record VehiculoDivisionPdf(Guid Id, string Nombre, string NumeroPlaca, Guid EmpresaDuenaId);

public record TipoDocumentoDivisionPdf(
    Guid Id, string Nombre, AmbitoAplicacion Ambito, bool VencimientoAutomatico, int? VigenciaMeses, IReadOnlyList<string> Aliases);

/// <summary>
/// Lo que el usuario puede elegir en Dividir para una Empresa gestionada, y también contra lo que se reconoce un PDF
/// y se comprueba lo que llega al crear. Es una sola lista para que las tres cosas no puedan discrepar.
/// </summary>
public record CatalogoDivisionPdf(
    EmpresaDivisionPdf EmpresaGestionada,
    IReadOnlyList<EmpresaDivisionPdf> Subcontratas,
    IReadOnlyList<TrabajadorDivisionPdf> Trabajadores,
    IReadOnlyList<VehiculoDivisionPdf> Vehiculos,
    IReadOnlyList<TipoDocumentoDivisionPdf> Tipos)
{
    public bool Contiene(ClaseTitular clase, Guid titularId) => clase switch
    {
        ClaseTitular.EmpresaGestionada => EmpresaGestionada.Id == titularId,
        ClaseTitular.Subcontrata => Subcontratas.Any(s => s.Id == titularId),
        ClaseTitular.Trabajador => Trabajadores.Any(t => t.Id == titularId),
        ClaseTitular.Vehiculo => Vehiculos.Any(v => v.Id == titularId),
        _ => false
    };

    /// <summary>La Empresa gestionada o Subcontrata de la que cuelga un titular; para el «Empresa a la que pertenece».</summary>
    public Guid? EmpresaDe(ClaseTitular clase, Guid titularId) => clase switch
    {
        ClaseTitular.EmpresaGestionada or ClaseTitular.Subcontrata => titularId,
        ClaseTitular.Trabajador => Trabajadores.FirstOrDefault(t => t.Id == titularId)?.EmpresaDuenaId,
        ClaseTitular.Vehiculo => Vehiculos.FirstOrDefault(v => v.Id == titularId)?.EmpresaDuenaId,
        _ => null
    };
}

/// <summary>
/// Una sugerencia de la detección, nunca un dato confirmado: nada se crea sin pasar por la revisión. No lleva
/// porcentajes a propósito: el motivo legible es lo que permite a una persona juzgarla.
/// </summary>
/// <param name="PaginaMotivo">Página en la que se leyó lo que dice <see cref="Motivo"/>, si procede.</param>
public record TramoDetectado(
    int PaginaInicial, int PaginaFinal,
    Guid? TipoDocumentoId, string? TipoNombre,
    ClaseTitular? Clase, Guid? TitularId, string? TitularNombre,
    MotivoDeteccion Motivo, int? PaginaMotivo);
