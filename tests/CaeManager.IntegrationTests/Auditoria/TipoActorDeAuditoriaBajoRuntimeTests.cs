using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// <b>La auditoría conserva QUÉ CLASE de actor provocó el cambio</b> (P41c,
/// decisión del propietario 2026-09-19: sin distinción visual todavía, pero
/// persistido para poder mostrarlo más adelante sin migración).
///
/// <para>
/// Bajo <c>cae_app_runtime</c> y no contra el rol propietario, a propósito: la
/// columna nueva vive en las dos tablas que tienen política RLS, y un test que
/// conectara como propietario demostraría que el interceptor calcula bien el
/// valor sin demostrar que la fila llega a escribirse — el falso verde que
/// <c>PropagacionTenantRlsSinSesionTests</c> documenta para esta misma tabla.
/// </para>
///
/// <para>
/// <b>Lo que aísla cada test.</b> Los dos primeros usan el MISMO actor con
/// identidad resuelta; lo único que cambia entre ellos es el ámbito ambiental de
/// servicio de fondo. Así el resultado no puede explicarse por "el de fondo no
/// tenía usuario": si el ámbito no mandara sobre la identidad resuelta, el
/// segundo test saldría <c>Persona</c>. Esa es exactamente la condición que hace
/// falta para que una petición con clave de API no pase por persona.
/// </para>
/// </summary>
public class TipoActorDeAuditoriaBajoRuntimeTests
{
    [Fact]
    public async Task Una_accion_de_usuario_queda_como_Persona()
    {
        var usuarioId = Guid.NewGuid();

        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            actorAuditoriaPersonalizado: new ActorFijo(ActorAuditoria.Normal(usuarioId)));

        await EscribirEmpresaAsync(arnes, "Persona auditada", "B12345674");

        var registro = await ObtenerRegistroDeEmpresaAsync(arnes.CadenaPropietario);

        registro.TipoActor.Should().Be(TipoActorAuditoria.Persona,
            "hay un usuario autenticado detrás del cambio y nadie declaró otra cosa");
        registro.ActorRealUsuarioId.Should().Be(usuarioId,
            "y el eje nuevo no sustituye a la identidad: siguen siendo dos preguntas distintas");
    }

    [Fact]
    public async Task Una_accion_de_servicio_de_fondo_queda_como_Sistema()
    {
        // El MISMO actor resuelto que el test de arriba: la única diferencia es
        // el ámbito declarado, y eso es lo que hace concluyente la comparación.
        var usuarioId = Guid.NewGuid();

        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            actorAuditoriaPersonalizado: new ActorFijo(ActorAuditoria.Normal(usuarioId)));

        using (AmbitoActorAuditoria.EstablecerSistema())
        {
            await EscribirEmpresaAsync(arnes, "Barrido auditado", "B12345674");
        }

        var registro = await ObtenerRegistroDeEmpresaAsync(arnes.CadenaPropietario);

        registro.TipoActor.Should().Be(TipoActorAuditoria.Sistema,
            "lo escribió la propia plataforma, y el ámbito declarado manda sobre la identidad que " +
            "hubiera resuelta — sin ese orden, una clave de API pasaría por persona");
    }

    /// <summary>
    /// El tercer caso, que es el que justifica que <c>Desconocido</c> exista
    /// como valor propio: sin ámbito declarado Y sin identidad resuelta, el
    /// registro dice "no lo sé" en vez de afirmar máquina. Es el estado de una
    /// persona cuyo guardado síncrono no llegó a resolver los claims, y
    /// llamarlo <c>Sistema</c> sería una afirmación falsa.
    /// </summary>
    [Fact]
    public async Task Sin_ambito_y_sin_identidad_resuelta_queda_Desconocido_y_no_Sistema()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        await EscribirEmpresaAsync(arnes, "Sin identidad auditada", "B12345674");

        var registro = await ObtenerRegistroDeEmpresaAsync(arnes.CadenaPropietario);

        registro.TipoActor.Should().Be(TipoActorAuditoria.Desconocido);
    }

    /// <summary>
    /// La migración que añade la columna no toca las políticas, y esto lo
    /// comprueba en vez de darlo por supuesto: un <c>ADD COLUMN</c> no las
    /// altera, pero "no debería" y "no lo hace" son afirmaciones distintas, y
    /// esta tabla es la que ya se rompió una vez contra su propia política.
    /// </summary>
    /// <summary>
    /// El caso de Stripe, medido: un webhook es <c>AllowAnonymous</c>, no hay
    /// identidad de sesión, y sin el filtro su escritura caía en
    /// <c>Desconocido</c>. Con el filtro del grupo, la misma escritura queda como
    /// integración externa.
    /// </summary>
    [Fact]
    public async Task Un_webhook_anonimo_bajo_el_filtro_de_grupo_queda_como_IntegracionExterna()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        await EjecutarBajoElFiltroAsync(() => EscribirEmpresaAsync(arnes, "Webhook auditado", "B12345674"));

        var registro = await ObtenerRegistroDeEmpresaAsync(arnes.CadenaPropietario);

        registro.TipoActor.Should().Be(TipoActorAuditoria.IntegracionExterna,
            "lo escribe el sistema de un tercero acreditado por firma, no una persona sin identificar");
    }

    /// <summary>
    /// El caso que el filtro previene en <c>/api/v1</c>: con clave de API el handler
    /// mete el Id de la CLAVE como identidad, y sin ámbito declarado esa escritura
    /// se resolvería como <c>Persona</c>. El actor resuelto es el mismo que en el
    /// test de Persona; solo cambia el filtro, y eso es lo que hace concluyente la
    /// comparación.
    /// </summary>
    [Fact]
    public async Task Una_llamada_con_clave_de_API_bajo_el_filtro_no_pasa_por_Persona()
    {
        var idDeLaClave = Guid.NewGuid();

        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            actorAuditoriaPersonalizado: new ActorFijo(ActorAuditoria.Normal(idDeLaClave)));

        await EjecutarBajoElFiltroAsync(() => EscribirEmpresaAsync(arnes, "Clave auditada", "B12345674"));

        var registro = await ObtenerRegistroDeEmpresaAsync(arnes.CadenaPropietario);

        registro.TipoActor.Should().Be(TipoActorAuditoria.IntegracionExterna,
            "el ámbito declarado manda sobre la identidad resuelta");
    }

    /// <summary>
    /// Lo que este test SÍ observa: durante la petición el ámbito está declarado, y
    /// después el hilo que llamó no lo ve. Lo que NO observa —y una mutación sin
    /// <c>using</c> en el filtro lo demostró: seguía verde— es que el <c>using</c>
    /// del filtro libere el ámbito. No es observable desde fuera de un método
    /// <c>async</c>: el contexto de ejecución del llamador queda aislado de lo que
    /// el método asigne a un <c>AsyncLocal</c>, con o sin <c>using</c>. La liberación
    /// del propio ámbito la prueba <c>AmbitoActorAuditoriaTests</c>, donde sí se
    /// puede observar.
    /// </summary>
    [Fact]
    public async Task El_filtro_declara_el_ambito_durante_la_peticion_y_no_lo_deja_en_el_hilo_que_llama()
    {
        AmbitoActorAuditoria.TipoActorActual.Should().BeNull("punto de partida: ningún ámbito declarado");

        var filtro = new ActorIntegracionExternaEndpointFilter();
        var contexto = new DefaultEndpointFilterInvocationContext(new DefaultHttpContext());

        TipoActor? dentro = null;
        var accion = async () => await filtro.InvokeAsync(contexto, _ =>
        {
            dentro = AmbitoActorAuditoria.TipoActorActual;
            throw new InvalidOperationException("el endpoint falla");
        });

        await accion.Should().ThrowAsync<InvalidOperationException>();

        dentro.Should().Be(TipoActor.IntegracionExterna, "durante la petición el ámbito está declarado");
        AmbitoActorAuditoria.TipoActorActual.Should().BeNull(
            "ni siquiera un endpoint que lance deja el ámbito puesto en el hilo que llamó");
    }

    private static async Task EjecutarBajoElFiltroAsync(Func<Task> endpoint)
    {
        var filtro = new ActorIntegracionExternaEndpointFilter();
        var contexto = new DefaultEndpointFilterInvocationContext(new DefaultHttpContext());

        await filtro.InvokeAsync(contexto, async _ =>
        {
            await endpoint();
            return null;
        });
    }

    [Fact]
    public async Task La_columna_nueva_no_deja_las_dos_tablas_sin_politica_de_aislamiento()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();

        foreach (var tabla in new[] { "RegistrosAuditoria", "RegistrosAccesoDocumentoSensible" })
        {
            await using var orden = new NpgsqlCommand(
                """
                SELECT count(*) FROM pg_policies
                WHERE schemaname = 'public' AND tablename = @tabla AND policyname = 'aislamiento_tenant'
                """, conexion);
            orden.Parameters.AddWithValue("tabla", tabla);

            var politicas = Convert.ToInt32(await orden.ExecuteScalarAsync());

            politicas.Should().Be(1, $"la tabla {tabla} tiene que seguir con su política de aislamiento por tenant");
        }
    }

    private static async Task EscribirEmpresaAsync(ArnesDeArranqueRuntime arnes, string nombre, string cif)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        using var ambitoTenant = AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto);

        contexto.Empresas.Add(Empresa.CrearComoCliente(
            nombre, cif, esCritico: false, notas: null, ejecutivoUsuarioId: null));

        await contexto.SaveChangesAsync();
    }

    /// <summary>
    /// Se lee como propietario, sin RLS ni filtro global de EF: la pregunta del
    /// test es "¿qué quedó escrito?", y leerlo con el mismo filtro que podría
    /// esconderlo convertiría una fila ausente en un verde.
    /// </summary>
    private static async Task<(TipoActorAuditoria TipoActor, Guid? ActorRealUsuarioId)> ObtenerRegistroDeEmpresaAsync(
        string cadenaPropietario)
    {
        await using var conexion = new NpgsqlConnection(cadenaPropietario);
        await conexion.OpenAsync();

        await using var orden = new NpgsqlCommand(
            """
            SELECT "TipoActor", "ActorRealUsuarioId" FROM "RegistrosAuditoria"
            WHERE "EntidadTipo" = 'Empresa'
            ORDER BY "FechaUtc" DESC
            LIMIT 1
            """, conexion);

        await using var lector = await orden.ExecuteReaderAsync();

        (await lector.ReadAsync()).Should().BeTrue(
            "sin fila de auditoría no hay nada que medir, y un vacío aquí no es un 'Desconocido'");

        return (
            Enum.Parse<TipoActorAuditoria>(lector.GetString(0)),
            await lector.IsDBNullAsync(1) ? null : lector.GetGuid(1));
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}
