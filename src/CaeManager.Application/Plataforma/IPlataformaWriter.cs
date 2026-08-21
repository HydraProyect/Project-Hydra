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
/// Ojo con lo que <b>no</b> hay aquí: no existe un método para crear
/// concesiones. Es deliberado. Una concesión nace de un acto explícito de
/// concesión, y ese acto —quién puede conceder, a quién, qué capacidad, bajo qué
/// autorización— es un contrato propio que todavía no está fijado. Mientras
/// tanto, el <c>WITH CHECK</c> de RLS (F2b-5) solo admite filas que nombren al
/// propio usuario de la sesión, así que la auto-concesión que el ADR § 4bis.7.7
/// acepta con equipo unipersonal es lo único representable.
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
    /// El único invocante legítimo es la auto-concesión, y un test de
    /// arquitectura mantiene esa lista en uno. No existe aquí una operación
    /// genérica de conceder a terceros: eso exige un contrato propio —quién
    /// concede, a quién, qué capacidad, sobre qué tenants, cómo se revoca y cómo
    /// se audita— y relajar el <c>WITH CHECK</c> de RLS, que hoy solo admite
    /// filas que nombren al propio usuario de la sesión.
    /// </summary>
    void AnadirConcesion(ConcesionPrivilegio concesion);
}
