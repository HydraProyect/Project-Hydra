using CaeManager.Domain.Common;

namespace CaeManager.Domain.Empresas;

/// <summary>
/// Oficio de una Empresa (decisión de Chris, 2026-10-18): el catálogo de oficios con el que la Empresa clasifica a sus
/// Trabajadores («Soldador», «Administrativo»). Sustituye en pantalla al texto libre «Puesto» del Trabajador, que se
/// conserva como dato histórico; la migración <c>AnadeDelegacionYOficioEmpresa</c> creó un Oficio por cada Puesto
/// distinto de cada Empresa. La fila es del Tenant propietario de la Empresa.
/// </summary>
public class OficioEmpresa : EntidadBase
{
    public const int LongitudMaximaNombre = 150;

    public Guid EmpresaId { get; private set; }
    public string Nombre { get; private set; } = string.Empty;

    private OficioEmpresa() { }

    public OficioEmpresa(Guid empresaId, string nombre)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("El oficio debe pertenecer a una empresa.", nameof(empresaId));

        EmpresaId = empresaId;
        Renombrar(nombre);
    }

    public void Renombrar(string nombre) => Nombre = NombreDeCatalogo.Normalizar(nombre, LongitudMaximaNombre, "el oficio");
}
