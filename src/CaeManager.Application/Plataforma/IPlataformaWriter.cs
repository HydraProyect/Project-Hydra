using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Plataforma;

/// <summary>
/// Escritura del plano de privilegio de plataforma.
///
/// Separado de <see cref="IPlataformaQueryContext"/>, que es solo de lectura,
/// por el mismo motivo por el que aquel existe: estas tablas dicen qué usuario
/// de TALVEG puede abrir los datos de qué cliente y hasta cuándo, y el conjunto
/// de sitios que las tocan tiene que caber en una revisión. Un test de
/// arquitectura vigila esa lista.
///
/// <b>No guarda.</b> Deja la entidad en el contexto para que el
/// <c>SaveChangesAsync</c> del comando la confirme — mismo patrón que
/// <c>IAsignacionesOperativasWriter</c>.
///
/// <see cref="AnadirConcesion"/> no es un método genérico de "conceder
/// cualquier capacidad a cualquiera": los dos únicos invocantes legítimos, y
/// por qué cada uno puede escribir aquí, se documentan en ese método.
/// </summary>
public interface IPlataformaWriter
{
    /// <summary>
    /// Añade una sesión ya construida por el dominio. Recibe el agregado, no sus
    /// campos: los siete invariantes de <c>SesionPrivilegiada.Abrir</c> tienen
    /// que haberse evaluado antes de llegar aquí.
    /// </summary>
    void AnadirSesion(SesionPrivilegiada sesion);

    /// <summary>
    /// Añade una concesión ya construida. Igual que arriba: recibe el agregado,
    /// no sus campos.
    ///
    /// Dos invocantes legítimos, y un test de arquitectura
    /// (<c>ConcesionesSoloPorActoExplicitoTests</c>) mantiene esa lista
    /// exacta: <c>AutoConcederPrivilegioCommand</c> (yo → yo, sin
    /// beneficiario como parámetro) y <c>ConcederPrivilegioCommand</c> (un
    /// AdminPlataforma → un tercero, solo capacidad Aprovisionamiento, PD-A3).
    /// El segundo exigió relajar el <c>WITH CHECK</c> de RLS
    /// (<c>RlsConcesionPorAdminDePlataforma</c>), que hasta entonces solo
    /// admitía filas que nombraran al propio usuario de la sesión — no hay
    /// operación genérica de "conceder cualquier capacidad a cualquiera": eso
    /// sigue exigiendo su propio contrato el día que haga falta.
    /// </summary>
    void AnadirConcesion(ConcesionPrivilegio concesion);
}
