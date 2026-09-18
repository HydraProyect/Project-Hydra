using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Npgsql;

namespace CaeManager.Web.Tests;

/// <summary>
/// REC-166: distinguir "el circuito de Blazor se desconectó con una consulta
/// en vuelo" de "PostgreSQL falló de verdad" o "la base es inalcanzable".
/// Ver Project-Hydra-Negocio/tecnico/reconciliacion/informes/REC-166-caracterizacion-2026-09-18.md.
///
/// <para>
/// <b>Lo que SÍ observa:</b> el predicado puro, contra instancias reales de
/// los tipos de Npgsql 10.0.3 — no contra dobles ni supuestos sobre su
/// jerarquía.
/// </para>
/// <para>
/// <b>Lo que NO observa:</b> que un componente concreto lo use correctamente
/// en su <c>catch</c> (eso lo cubren <see cref="NotificacionesPopupTests"/>),
/// ni si Npgsql cambia esta jerarquía en una versión futura — un cambio ahí
/// solo lo detectaría una ejecución real contra PostgreSQL.
/// </para>
/// </summary>
public class ExcepcionDeCircuitoDesconectadoTests
{
    [Fact]
    public void ObjectDisposedException_es_la_carrera()
    {
        ExcepcionDeCircuitoDesconectado.Es(new ObjectDisposedException("CaeManagerDbContext")).Should().BeTrue();
    }

    [Fact]
    public void ArgumentOutOfRangeException_es_la_carrera()
    {
        ExcepcionDeCircuitoDesconectado.Es(new ArgumentOutOfRangeException("index")).Should().BeTrue();
    }

    [Fact]
    public void NpgsqlException_cruda_de_desincronizacion_de_protocolo_es_la_carrera()
    {
        // Firma real medida en CI (REC-166): construida sin InnerException,
        // igual que Npgsql.Util.Statics.ThrowIfMsgWrongType.
        var ex = new NpgsqlException("Received backend message BindComplete while expecting ParseCompleteMessage. Please file a bug.");

        ExcepcionDeCircuitoDesconectado.Es(ex).Should().BeTrue();
    }

    [Fact]
    public void PostgresException_nunca_se_traga_aunque_sea_del_mismo_tipo_base()
    {
        var ex = new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505");

        ExcepcionDeCircuitoDesconectado.Es(ex).Should().BeFalse();
    }

    [Fact]
    public void NpgsqlException_transitoria_de_red_no_se_traga()
    {
        // IsTransient == true exige un InnerException de tipo IOException,
        // SocketException, TimeoutException o NpgsqlException transitoria
        // (NpgsqlException.cs, Npgsql 10.0.3) — la base inalcanzable cae aquí,
        // no en la rama de arriba.
        var ex = new NpgsqlException("Exception while reading from stream", new IOException("Connection reset by peer"));

        ExcepcionDeCircuitoDesconectado.Es(ex).Should().BeFalse();
    }

    [Fact]
    public void NpgsqlOperationInProgressException_es_un_bug_de_concurrencia_real_no_se_traga()
    {
        var ex = new NpgsqlOperationInProgressException(new NpgsqlCommand("SELECT 1"));

        ExcepcionDeCircuitoDesconectado.Es(ex).Should().BeFalse();
    }

    [Fact]
    public void Una_excepcion_sin_relacion_no_se_traga()
    {
        ExcepcionDeCircuitoDesconectado.Es(new InvalidOperationException("nada que ver")).Should().BeFalse();
    }
}
