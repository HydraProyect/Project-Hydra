using CaeManager.Domain.Common;

namespace CaeManager.Domain.Asignaciones;

/// <summary>
/// Relación entre un Trabajador y un Centro, con fecha de alta/baja. Mejora
/// directa sobre la matriz de "X" del Excel original, que no guardaba
/// historial de fechas (ver DATABASE.md).
/// </summary>
public class Asignacion : EntidadConTenant
{
    public Guid TrabajadorId { get; private set; }
    public Guid CentroId { get; private set; }
    public DateOnly FechaAlta { get; private set; }
    public DateOnly? FechaBaja { get; private set; }

    public bool EstaActiva => FechaBaja is null;

    private Asignacion()
    {
    }

    public Asignacion(Guid trabajadorId, Guid centroId, DateOnly fechaAlta)
    {
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("La asignación debe tener un trabajador.", nameof(trabajadorId));
        if (centroId == Guid.Empty)
            throw new ArgumentException("La asignación debe tener un centro.", nameof(centroId));

        TrabajadorId = trabajadorId;
        CentroId = centroId;
        FechaAlta = fechaAlta;
    }

    public void DarDeBaja(DateOnly fechaBaja)
    {
        if (fechaBaja < FechaAlta)
            throw new ArgumentException("La fecha de baja no puede ser anterior a la fecha de alta.", nameof(fechaBaja));

        FechaBaja = fechaBaja;
    }

    public void ReactivarAlta()
    {
        FechaBaja = null;
    }

    /// <summary>
    /// Si el rango [FechaAlta, FechaBaja) de esta asignación se solapa con el
    /// de otra del mismo trío (Tenant, Trabajador, Centro) — DEC-19: dos
    /// vigencias solapadas para la misma coordenada semántica son una
    /// contradicción de datos, no una posibilidad legítima (turnos o
    /// proyectos se representan como ejes propios del dominio, no como
    /// duplicados temporales).
    /// </summary>
    /// <remarks>
    /// El límite superior es EXCLUSIVO a propósito: dar de baja hoy y
    /// reasignar hoy mismo (mismo trío) no es un solape — ver
    /// ReasignarMismoDiaTests, cuyo bug real (23505 de Postgres) esta
    /// invariante no puede reintroducir. Una baja abierta (<c>null</c>) se
    /// trata como horizonte infinito.
    ///
    /// Un rango VACÍO (<c>FechaAlta == FechaBaja</c>, el que deja
    /// <see cref="CerrarPorAmbitoEliminado"/> al anclar la baja a una alta
    /// futura, o un <see cref="DarDeBaja"/> el mismo día del alta) no se
    /// solapa con nada — no contiene ni un solo día. Sin este caso aparte, la
    /// fórmula de rangos semiabiertos de más abajo lo trataría como si
    /// ocupara todo <c>[FechaAlta, ∞)</c> (revisión de Codex, REC-064):
    /// PostgreSQL normaliza <c>daterange(d, d, '[)')</c> como vacío de forma
    /// nativa, así que sin este guard la aplicación rechazaría altas que la
    /// restricción de la base sí permitiría.
    /// </remarks>
    public bool SeSolapaCon(DateOnly otraFechaAlta, DateOnly? otraFechaBaja)
    {
        if (FechaBaja == FechaAlta) return false;
        if (otraFechaBaja == otraFechaAlta) return false;

        var estaBajaEfectiva = FechaBaja ?? DateOnly.MaxValue;
        var otraBajaEfectiva = otraFechaBaja ?? DateOnly.MaxValue;

        return FechaAlta < otraBajaEfectiva && otraFechaAlta < estaBajaEfectiva;
    }

    /// <summary>
    /// Cierre derivado de que desaparezca uno de los dos extremos de la
    /// asignación: el centro o el trabajador pasan a <c>EstaEliminado</c>.
    /// </summary>
    /// <remarks>
    /// No es una baja que nadie decida sobre esta asignación —es la
    /// consecuencia de que deje de existir aquello que la sostiene—, y por
    /// eso <b>no puede fallar por fecha</b>: si el alta era futura, la baja
    /// se ancla al alta y la asignación queda cerrada el mismo día que se
    /// abrió. <see cref="DarDeBaja"/> sí valida, porque allí la fecha la
    /// elige una persona y una fecha anterior al alta es un error suyo;
    /// aquí no hay nadie a quien devolverle el error, y lanzar dejaría el
    /// borrado a medias con la asignación viva colgando de un centro
    /// muerto — justo la violación que este cierre existe para impedir.
    /// </remarks>
    public void CerrarPorAmbitoEliminado(DateOnly fecha)
    {
        if (FechaBaja is not null) return;

        FechaBaja = fecha < FechaAlta ? FechaAlta : fecha;
    }
}
