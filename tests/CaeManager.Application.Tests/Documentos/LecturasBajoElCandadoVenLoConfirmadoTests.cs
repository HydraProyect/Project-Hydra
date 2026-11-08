using System.Linq.Expressions;
using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Commands.ReactivarAsignacion;
using CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Tests.Asignaciones;
using CaeManager.Application.Tests.Centros;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Query;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Revisión Codex de la PR #2074, ronda 4: el candado ordena las altas, pero no refresca lo que el contexto (uno por
/// circuito en Blazor Server) rastreó antes de esperar. Cada prueba deja rastreada una fila antes del candado, confirma
/// una escritura ajena mientras se espera (<see cref="SerializacionAltasAcreditacionEmpresaFalsa.AlTomarElCandado"/>) y
/// comprueba que lo que se decide y se guarda bajo el candado parte de lo confirmado. El rastreo se simula con
/// <see cref="RequisitosRastreadosComoEf"/>: una consulta de entidades devuelve la copia rastreada, con sus valores de
/// entonces, y una proyección lee la base, como EF. Que la relectura de verdad refresque la instancia lo mide la suite
/// de integración (<c>LecturasActualizadasBajoElCandadoTests</c>).
/// </summary>
public class LecturasBajoElCandadoVenLoConfirmadoTests
{
    private static readonly DateOnly Hoy = new(2026, 1, 15);

    [Fact]
    public async Task El_alta_de_un_documento_de_empresa_ve_el_requisito_que_otra_operacion_confirmo_mientras_esperaba_aunque_el_contexto_rastreara_el_viejo()
    {
        // Hallazgo 1: el contexto rastreó la exclusión (Incluido=false) antes del candado; otra operación exige el tipo
        // y confirma cuando aún no existe el Documento, así que no crea el par. Solo si el alta lee lo confirmado, lo crea.
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Seguro RC");
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta propia", mundo.EmpresaId));
        var fila = new TipoDocumentoCentro(tipo.Id, acceso.CentroId, incluido: false);
        mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros.Add(fila);
        var rastreo = new RequisitosRastreadosComoEf(mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros);
        rastreo.Rastrear(fila);
        var servicio = new AltaAcreditacionesPlataformaService(
            mundo.AsignacionesContexto, mundo.CentrosContexto, mundo.TrabajadoresContexto, mundo.DocumentosContexto,
            rastreo.Contexto(mundo.TiposDocumentoContexto), mundo.Acreditaciones);
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa
        {
            AlTomarElCandado = () => RequisitosRastreadosComoEf.EscribirAjena(fila, incluido: true)
        };
        var documento = Documento.DeEmpresa(mundo.EmpresaId, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(5)));

        await serializacion.EjecutarAsync(async ct =>
        {
            await servicio.AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] }, ct);
            return Result.Exito();
        });

        rastreo.Rastreada(fila).Incluido.Should().BeFalse("la copia rastreada sigue con el valor de antes del candado");
        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)]);
    }

    [Fact]
    public async Task La_relectura_del_doble_repone_todos_los_campos_que_otra_operacion_confirmo_y_no_deja_nada_que_guardar()
    {
        // Issue #2094, B3: el doble solo refrescaba Incluido, así que la copia releída conservaba la periodicidad, el
        // archivo o la tolerancia de antes del candado y un guardado posterior podía escribirlos sobre lo confirmado.
        var tipoId = Guid.NewGuid();
        var centroId = Guid.NewGuid();
        var fila = new TipoDocumentoCentro(tipoId, centroId, true, null, false, null, null, null);
        var confirmadas = new List<TipoDocumentoCentro> { fila };
        var rastreo = new RequisitosRastreadosComoEf(confirmadas);
        rastreo.Rastrear(fila);
        fila.Actualizar(false, 6, true, "t/otro.pdf", "otro.pdf", 10);

        var releida = await rastreo.ObtenerPorParActualizadoAsync(tipoId, centroId);

        releida.Should().BeSameAs(rastreo.Rastreada(fila));
        releida.Should().BeEquivalentTo(new
        {
            Incluido = false,
            PeriodicidadEspecialMeses = (int?)6,
            BloqueaAcceso = true,
            ArchivoUrl = "t/otro.pdf",
            NombreArchivoOriginal = "otro.pdf",
            ToleranciaDias = (int?)10
        });
        (await rastreo.SaveChangesAsync()).Should().Be(0, "la copia releída coincide con lo confirmado");
    }

    [Fact]
    public async Task Exigir_un_tipo_que_otra_operacion_excluyo_mientras_esperaba_guarda_la_exigencia_aunque_el_contexto_rastreara_la_fila_exigida()
    {
        // Hallazgo 2: la fila rastreada dice Incluido=true; otra operación la excluye y confirma; esta pide true. Con la
        // copia vieja no hay cambio para EF, el comando devuelve éxito y la base se queda en false.
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Seguro RC");
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta propia", mundo.EmpresaId));
        var fila = new TipoDocumentoCentro(tipo.Id, acceso.CentroId, incluido: true);
        mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros.Add(fila);
        var rastreo = new RequisitosRastreadosComoEf(mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros);
        rastreo.Rastrear(fila);
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa
        {
            AlTomarElCandado = () => RequisitosRastreadosComoEf.EscribirAjena(fila, incluido: false)
        };
        var handler = Handler(mundo, rastreo, serializacion);

        var resultado = await handler.Handle(Peticion(acceso.CentroId, tipo.Id, incluido: true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        serializacion.Llamadas.Should().Be(1);
        fila.Incluido.Should().BeTrue("la exigencia se guarda sobre lo que la otra operación confirmó");
    }

    [Fact]
    public async Task Excluir_un_tipo_toma_el_candado_y_guarda_la_exclusion_sobre_la_exigencia_que_otra_operacion_confirmo_mientras_esperaba()
    {
        // La rama que excluye escribe la misma fila: sin candado no se ordena con la exigencia, y con la copia vieja
        // (Incluido=false) pedir false no sería un cambio y la exigencia ajena se quedaría.
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Seguro RC");
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta propia", mundo.EmpresaId));
        var fila = new TipoDocumentoCentro(tipo.Id, acceso.CentroId, incluido: false);
        mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros.Add(fila);
        var rastreo = new RequisitosRastreadosComoEf(mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros);
        rastreo.Rastrear(fila);
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa
        {
            AlTomarElCandado = () => RequisitosRastreadosComoEf.EscribirAjena(fila, incluido: true)
        };
        var handler = Handler(mundo, rastreo, serializacion);

        var resultado = await handler.Handle(Peticion(acceso.CentroId, tipo.Id, incluido: false), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        serializacion.Llamadas.Should().Be(1, "dejar de exigir escribe la misma fila y se serializa con exigirla");
        fila.Incluido.Should().BeFalse("la exclusión se guarda sobre lo que la otra operación confirmó");
    }

    [Fact]
    public async Task Crear_una_asignacion_comprueba_bajo_el_candado_si_otra_alta_del_mismo_trio_confirmo_mientras_esperaba()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta de otra Empresa"));
        var repositorio = new AsignacionRepositorioFalso();
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa
        {
            AlTomarElCandado = () => repositorio.Asignaciones.Add(new Asignacion(trabajador.Id, acceso.CentroId, Hoy))
        };
        var handler = new CrearAsignacionCommandHandler(repositorio, new AutoridadTotal(), mundo.Servicio(), new UnitOfWorkFalso(), serializacion);

        var resultado = await handler.Handle(new CrearAsignacionCommand(trabajador.Id, acceso.CentroId, Hoy), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Asignacion.YaActiva");
        repositorio.Asignaciones.Should().HaveCount(1, "solo queda la Asignación que confirmó la otra alta");
    }

    [Fact]
    public async Task Reabrir_una_asignacion_relee_bajo_el_candado_lo_confirmado_y_comprueba_ahi_si_otra_ya_esta_activa()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta de otra Empresa"));
        var repositorio = new AsignacionRepositorioFalso();
        var cerrada = new Asignacion(trabajador.Id, acceso.CentroId, Hoy.AddDays(-30));
        cerrada.DarDeBaja(Hoy.AddDays(-10));
        repositorio.Asignaciones.Add(cerrada);
        var lecturasAntesDelCandado = -1;
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa
        {
            AlTomarElCandado = () =>
            {
                lecturasAntesDelCandado = repositorio.LecturasActualizadas;
                repositorio.Asignaciones.Add(new Asignacion(trabajador.Id, acceso.CentroId, Hoy));
            }
        };
        var handler = new ReactivarAsignacionCommandHandler(repositorio, new AutoridadTotal(), mundo.Servicio(), new UnitOfWorkFalso(), serializacion);

        var resultado = await handler.Handle(new ReactivarAsignacionCommand(cerrada.Id), CancellationToken.None);

        repositorio.LecturasActualizadas.Should().BeGreaterThan(lecturasAntesDelCandado, "la Asignación se relee con lo confirmado bajo el candado");
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Asignacion.YaActiva");
        cerrada.EstaActiva.Should().BeFalse();
    }

    [Fact]
    public async Task Editar_un_tipo_lee_bajo_el_candado_el_tipo_y_sus_filas_con_lo_confirmado()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Seguro RC");
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta propia", mundo.EmpresaId));
        var fila = new TipoDocumentoCentro(tipo.Id, acceso.CentroId, incluido: true);
        mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros.Add(fila);
        var rastreo = new RequisitosRastreadosComoEf(mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros);
        rastreo.Rastrear(fila);
        var tipos = new TipoDocumentoRepositorioFalso();
        tipos.Agregar(tipo);
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa
        {
            // Otra edición quita el Centro y confirma; esta vuelve a marcarlo.
            AlTomarElCandado = () => RequisitosRastreadosComoEf.EscribirAjena(fila, incluido: false)
        };
        var handler = new EditarTipoDocumentoCommandHandler(
            tipos, rastreo, mundo.CentrosContexto, mundo.Servicio(), new AlcanceDatosServiceFalso(), rastreo, serializacion);

        var resultado = await handler.Handle(
            new EditarTipoDocumentoCommand(
                tipo.Id, tipo.Nombre, 12, true, 1, RequisitoDocumental.No, NaturalezaJuridica.RequisitoCliente,
                null, null, null, null, null, [acceso.CentroId]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        tipos.LecturasActualizadas.Should().Be(1);
        fila.Incluido.Should().BeTrue("volver a marcar el Centro se guarda sobre la baja que la otra edición confirmó");
    }

    private static EstablecerDocumentacionRequeridaCentroCommandHandler Handler(
        MundoAcreditaciones mundo, RequisitosRastreadosComoEf rastreo, SerializacionAltasAcreditacionEmpresaFalsa serializacion) =>
        new(rastreo, mundo.CentrosContexto, mundo.TiposDocumentoContexto, mundo.Servicio(), new AlcanceDatosServiceFalso(),
            rastreo, new VerificadorArchivoAlmacenadoPermisivo(), serializacion);

    private static EstablecerDocumentacionRequeridaCentroCommand Peticion(Guid centroId, Guid tipoId, bool incluido) =>
        new(centroId, tipoId, Incluido: incluido, null, false, null, null, ToleranciaDias: null);

    private sealed class AutoridadTotal : IAutoridadAsignacionesService
    {
        public Task<bool> PuedeModificarAsignacionesDelCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> FiltrarCentrosConAutoridadAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(centroIds);

        public Task<bool> PuedeModificarAsignacionesDelTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> FiltrarTrabajadoresConAutoridadAsync(IReadOnlyList<Guid> trabajadorIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(trabajadorIds);
    }
}

/// <summary>
/// Doble del mapa de identidad de EF para <see cref="TipoDocumentoCentro"/>: la lista es la base confirmada y cada fila
/// leída como entidad se rastrea en una copia con los valores de entonces. Una consulta de entidades devuelve la copia
/// rastreada sin refrescarla; una proyección lee la base; <see cref="ObtenerPorParActualizadoAsync"/> refresca la copia;
/// y guardar solo escribe lo que difiere de los valores originales de la copia, como el seguimiento de cambios.
/// </summary>
internal sealed class RequisitosRastreadosComoEf(List<TipoDocumentoCentro> confirmadas) : ITipoDocumentoCentroRepository, IUnitOfWork
{
    private sealed record Valores(bool Incluido, int? Periodicidad, bool BloqueaAcceso, string? ArchivoUrl, string? Nombre, int? Tolerancia)
    {
        public static Valores De(TipoDocumentoCentro f) =>
            new(f.Incluido, f.PeriodicidadEspecialMeses, f.BloqueaAcceso, f.ArchivoUrl, f.NombreArchivoOriginal, f.ToleranciaDias);
    }

    private readonly Dictionary<(Guid, Guid), (TipoDocumentoCentro Copia, Valores Original, TipoDocumentoCentro Base)> rastreadas = [];

    /// <summary>Lo que hace otra operación con su propio contexto: cambia la fila confirmada, no la copia rastreada.</summary>
    public static void EscribirAjena(TipoDocumentoCentro confirmada, bool incluido) =>
        confirmada.Actualizar(incluido, confirmada.PeriodicidadEspecialMeses, confirmada.BloqueaAcceso,
            confirmada.ArchivoUrl, confirmada.NombreArchivoOriginal, confirmada.ToleranciaDias);

    public TipoDocumentoCentro Rastrear(TipoDocumentoCentro confirmada)
    {
        var par = (confirmada.TipoDocumentoId, confirmada.CentroId);
        if (rastreadas.TryGetValue(par, out var r))
            return r.Copia;

        var copia = new TipoDocumentoCentro(confirmada.TipoDocumentoId, confirmada.CentroId, confirmada.Incluido,
            confirmada.PeriodicidadEspecialMeses, confirmada.BloqueaAcceso, confirmada.ArchivoUrl,
            confirmada.NombreArchivoOriginal, confirmada.ToleranciaDias);
        rastreadas[par] = (copia, Valores.De(copia), confirmada);
        return copia;
    }

    public TipoDocumentoCentro Rastreada(TipoDocumentoCentro confirmada) =>
        rastreadas[(confirmada.TipoDocumentoId, confirmada.CentroId)].Copia;

    /// <summary>El contexto de consulta con <c>TiposDocumentoCentros</c> resuelto por identidad, como EF.</summary>
    public ITiposDocumentoQueryContext Contexto(TiposDocumentoQueryContextFalso interno) => new ContextoConIdentidad(interno, this);

    private object? Resolver(object? resultado) => resultado is TipoDocumentoCentro f && confirmadas.Contains(f) ? Rastrear(f) : resultado;

    /// <summary>
    /// Como la relectura de <c>ListarActualesAsync</c>: repone todos los campos de la copia, no solo <c>Incluido</c>
    /// (issue #2094, B3).
    /// </summary>
    private void Refrescar(TipoDocumentoCentro copia)
    {
        var par = (copia.TipoDocumentoId, copia.CentroId);
        var (_, _, confirmada) = rastreadas[par];
        copia.Actualizar(confirmada.Incluido, confirmada.PeriodicidadEspecialMeses, confirmada.BloqueaAcceso,
            confirmada.ArchivoUrl, confirmada.NombreArchivoOriginal, confirmada.ToleranciaDias);
        rastreadas[par] = (copia, Valores.De(confirmada), confirmada);
    }

    public Task<TipoDocumentoCentro?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorTipoDocumentoAsync(Guid tipoDocumentoId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TipoDocumentoCentro>>(
            confirmadas.Where(f => f.TipoDocumentoId == tipoDocumentoId).Select(Rastrear).ToList());

    public Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TipoDocumentoCentro>>(confirmadas.Where(f => f.CentroId == centroId).Select(Rastrear).ToList());

    public Task<TipoDocumentoCentro?> ObtenerPorParAsync(Guid tipoDocumentoId, Guid centroId, CancellationToken cancellationToken = default) =>
        Task.FromResult(confirmadas.Where(f => f.TipoDocumentoId == tipoDocumentoId && f.CentroId == centroId)
            .Select(Rastrear).FirstOrDefault());

    public async Task<TipoDocumentoCentro?> ObtenerPorParActualizadoAsync(Guid tipoDocumentoId, Guid centroId, CancellationToken cancellationToken = default)
    {
        var copia = await ObtenerPorParAsync(tipoDocumentoId, centroId, cancellationToken);
        if (copia is not null)
            Refrescar(copia);
        return copia;
    }

    public async Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorTipoDocumentoActualizadoAsync(Guid tipoDocumentoId, CancellationToken cancellationToken = default)
    {
        var copias = await ObtenerPorTipoDocumentoAsync(tipoDocumentoId, cancellationToken);
        foreach (var copia in copias)
            Refrescar(copia);
        return copias;
    }

    public void Agregar(TipoDocumentoCentro tipoDocumentoCentro) => confirmadas.Add(tipoDocumentoCentro);

    public void Eliminar(TipoDocumentoCentro tipoDocumentoCentro)
    {
        var par = (tipoDocumentoCentro.TipoDocumentoId, tipoDocumentoCentro.CentroId);
        if (rastreadas.Remove(par, out var r))
            confirmadas.Remove(r.Base);
        else
            confirmadas.Remove(tipoDocumentoCentro);
    }

    /// <summary>Como el seguimiento de cambios: solo se escribe la copia cuyos valores difieren de los originales.</summary>
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var escritas = 0;
        foreach (var par in rastreadas.Keys.ToList())
        {
            var (copia, original, confirmada) = rastreadas[par];
            if (Valores.De(copia) == original)
                continue;

            confirmada.Actualizar(copia.Incluido, copia.PeriodicidadEspecialMeses, copia.BloqueaAcceso,
                copia.ArchivoUrl, copia.NombreArchivoOriginal, copia.ToleranciaDias);
            rastreadas[par] = (copia, Valores.De(copia), confirmada);
            escritas++;
        }

        return Task.FromResult(escritas);
    }

    private sealed class ContextoConIdentidad(TiposDocumentoQueryContextFalso interno, RequisitosRastreadosComoEf rastreo) : ITiposDocumentoQueryContext
    {
        public IQueryable<Domain.Documentos.TipoDocumento> TiposDocumento => interno.TiposDocumento;
        public IQueryable<TipoDocumentoCentro> TiposDocumentoCentros =>
            new ConsultaConIdentidad<TipoDocumentoCentro>(interno.ListaTiposDocumentoCentros.AsQueryable(), rastreo.Resolver);
        public IQueryable<TipoDocumentoAlias> TiposDocumentoAlias => interno.TiposDocumentoAlias;
        public IQueryable<ConfiguracionIaDocumentoCliente> ConfiguracionesIaDocumentoCliente => interno.ConfiguracionesIaDocumentoCliente;
        public IQueryable<ToleranciaDocumentoClienteEmpresarial> ToleranciasDocumentoClienteEmpresarial => interno.ToleranciasDocumentoClienteEmpresarial;
    }
}

/// <summary>
/// Consulta que, al materializar, pasa cada resultado por <paramref name="resolver"/>: una entidad sale como la instancia
/// rastreada y una proyección, tal cual se leyó de la base.
/// </summary>
internal sealed class ConsultaConIdentidad<T>(IQueryable<T> interna, Func<object?, object?> resolver) : IOrderedQueryable<T>, IAsyncEnumerable<T>
{
    public Type ElementType => interna.ElementType;
    public Expression Expression => interna.Expression;
    public IQueryProvider Provider => new ProveedorConIdentidad(interna.Provider, resolver);

    public IEnumerator<T> GetEnumerator() => interna.AsEnumerable().Select(e => (T)resolver(e)!).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new TestAsyncEnumerator<T>(GetEnumerator());

    private sealed class ProveedorConIdentidad(IQueryProvider interno, Func<object?, object?> resolver) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(Expression expression)
        {
            var consulta = interno.CreateQuery(expression);
            return (IQueryable)Activator.CreateInstance(
                typeof(ConsultaConIdentidad<>).MakeGenericType(consulta.ElementType), consulta, resolver)!;
        }

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
            new ConsultaConIdentidad<TElement>(interno.CreateQuery<TElement>(expression), resolver);

        public object? Execute(Expression expression) => resolver(interno.Execute(expression));

        public TResult Execute<TResult>(Expression expression) => (TResult)resolver(interno.Execute<TResult>(expression))!;

        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var tipoResultado = typeof(TResult).GetGenericArguments().FirstOrDefault() ?? typeof(TResult);
            var resultado = resolver(interno.Execute(expression));
            var metodoFromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(tipoResultado);
            return (TResult)metodoFromResult.Invoke(null, [resultado])!;
        }
    }
}
