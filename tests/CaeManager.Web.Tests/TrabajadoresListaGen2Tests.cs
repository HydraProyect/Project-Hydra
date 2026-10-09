using CaeManager.Infrastructure.Identity;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajador;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajadores;
using CaeManager.Application.Trabajadores.Commands.RestaurarTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerEmpleadoresDeTrabajadoresVisibles;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lista de Trabajadores (/trabajadores) tras la fase 1 del rediseño de listados (maqueta
/// aprobada de listados interactivos; antes, el mockup Gen 2). El vacío por filtro y el menú
/// con «Abrir Trabajador 360», que ya existían y se conservan, los sigue probando
/// <see cref="TrabajadoresVacioPorFiltroTests"/>.
///
/// <para>
/// El doble del mediador guarda los trabajadores y APLICA lo que recibe:
/// busca como <c>ObtenerTrabajadoresQueryHandler</c> (campo a campo en
/// nombre, apellidos, DNI y alias, sin distinguir mayúsculas), filtra por
/// <c>EmpresaId</c>, <c>SubcontrataId</c> y <c>EstadoDocumental</c>, ordena
/// con el mismo reparto en dos caminos que el handler (por estado documental
/// si se filtra u ordena por él; si no, la misma lista blanca de columnas) con
/// el Id como desempate final, y pagina con <c>Pagina</c>/<c>TamanoPagina</c>.
/// Los comandos cambian lo guardado. Un doble que ignorase un parámetro
/// dejaría en verde una pantalla que no lo envía.
/// </para>
///
/// <para>
/// Lo que el doble NO reproduce, y por eso ningún test de aquí depende de
/// ello: el alcance de cartera (<c>IAlcanceDatosService</c>), la intercalación
/// de PostgreSQL (aquí se compara ordinal) y el orden de los <c>uuid</c> en
/// PostgreSQL, que no es el de <see cref="Guid"/> en .NET — el desempate por
/// Id solo garantiza aquí que el orden sea total, no cuál.
/// </para>
/// </summary>
public class TrabajadoresListaGen2Tests : BunitContext
{
    /// <summary>QuickGrid y AtajosListaTeclado importan sus módulos JS al montarse.</summary>
    public TrabajadoresListaGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private static readonly Guid EmpresaEbro = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EmpresaDexter = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SubcontrataNervion = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary><see cref="TrabajadorListaDto"/> no lleva el alias, pero el handler busca en él: va aparte.</summary>
    private sealed record Fila(TrabajadorListaDto Dto, Guid? EmpresaId, Guid? SubcontrataId, string? Alias = null);

    private sealed class MediatorFalso : IMediator
    {
        public List<Fila> Almacen { get; } = [];
        public int? EliminadosForzados { get; set; }
        /// <summary>Ids que el lote pide y no puede eliminar: vuelven como error y no entran en IdsEliminados.</summary>
        public HashSet<Guid> NoEliminables { get; } = [];
        private readonly List<Fila> _papelera = [];
        public List<object> Enviadas { get; } = [];

        public PerfilVocabularioTenant Perfil { get; set; } = PerfilVocabularioTenant.Consultora;
        /// <summary>Por defecto, un usuario mono-Tenant: sin selector ni cabecera de empresa gestionada.</summary>
        /// <summary>Si se fija, la respuesta de la lista de Tenants autorizados espera a esta tarea (mediador asíncrono).</summary>
        public Task? RetenerAutorizados { get; set; }

        /// <summary>El token con el que la página pidió la lista de Tenants autorizados (solo con <see cref="RetenerAutorizados"/>).</summary>
        public CancellationToken? TokenDeAutorizados { get; private set; }

        /// <summary>Si es cierto, la respuesta retenida ignora el token: simula una resolución que vuelve normal tras cancelarse.</summary>
        public bool IgnorarCancelacionDeAutorizados { get; set; }

        public List<ClienteAutorizadoDto> Autorizados { get; } = [new(Guid.NewGuid(), "Propia", EsOrigen: true)];
        /// <summary>false = Operador CAE externo trabajando en un Tenant beneficiario ajeno.</summary>
        public bool EsDelPropioTenant { get; set; } = true;
        public List<EmpresaSelectorDto> Empresas { get; } =
            [new(EmpresaEbro, "Montajes Ebro S.L."), new(EmpresaDexter, "Dexter Industrial S.A.")];
        public List<FiltroGuardadoDto> FiltrosGuardados { get; } = [];
        public List<SubcontrataSelectorDto> Subcontratas { get; } =
            [new(SubcontrataNervion, "Aislamientos Nervión S.L.")];

        /// <summary>
        /// Opciones de la pastilla «Empresa»: los empleadores de los Trabajadores visibles. De serie, los mismos
        /// que ofrece el alta, para que los tests del filtro no dependan de la diferencia; los de alcance la fijan.
        /// </summary>
        public EmpleadoresDeTrabajadoresDto? EmpleadoresFiltro { get; set; }

        /// <summary>La consulta de las opciones de la pastilla «Empresa» falla.</summary>
        public bool FallarEmpleadoresFiltro { get; set; }

        /// <summary>Lo que responde «Guardar filtro»; sin valor, éxito.</summary>
        public Result<Guid>? ResultadoGuardarFiltro { get; set; }

        public List<CentroSelectorDto> Centros { get; } = [];
        public Func<Guid, IReadOnlyList<DocumentoFaltanteDto>> Faltantes { get; set; } = _ => [];

        /// <summary>
        /// Si devuelve una tarea para la petición, esa es la respuesta: permite
        /// retenerla con un <see cref="TaskCompletionSource{TResult}"/> y
        /// resolverla fuera de orden.
        /// </summary>
        public Func<object, Task<object>?>? Retener { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);

            if (request is ObtenerClientesAutorizadosQuery && RetenerAutorizados is { } espera)
            {
                TokenDeAutorizados = cancellationToken;
                // Un mediador real lanza al cancelarse; uno que "vuelve" con el contexto por defecto es el caso
                // que la guarda de Dispose de la página debe cubrir por sí sola.
                await (IgnorarCancelacionDeAutorizados ? espera : espera.WaitAsync(cancellationToken));
            }

            if (Retener?.Invoke(request) is { } retenida)
                return (TResponse)await retenida;

            // Síncrono a propósito, como los demás dobles de lista: lo asíncrono
            // de verdad se prueba reteniendo con Retener.
            return (TResponse)Responder(request);
        }

        private object Responder(object request)
        {
            switch (request)
            {
                case UsaRotulosPrimeraPersonaQuery:
                    return Perfil == PerfilVocabularioTenant.ClienteDirecto && EsDelPropioTenant;
                case ObtenerPerfilVocabularioActualQuery:
                    return Perfil;
                case ObtenerClientesAutorizadosQuery:
                    return (IReadOnlyList<ClienteAutorizadoDto>)Autorizados.ToList();
                case ObtenerEmpresasParaSelectorQuery:
                    return (IReadOnlyList<EmpresaSelectorDto>)Empresas.ToList();
                case ObtenerSubcontratasParaSelectorQuery:
                    return (IReadOnlyList<SubcontrataSelectorDto>)Subcontratas.ToList();
                case ObtenerFiltrosGuardadosQuery:
                    return (IReadOnlyList<FiltroGuardadoDto>)FiltrosGuardados.ToList();
                case ObtenerEmpleadoresDeTrabajadoresVisiblesQuery:
                    if (FallarEmpleadoresFiltro)
                        throw new InvalidOperationException("Fallo simulado de las opciones de la pastilla Empresa.");
                    return EmpleadoresFiltro ?? new EmpleadoresDeTrabajadoresDto(
                        Empresas.Select(e => new EmpleadorDeTrabajadorDto(e.Id, e.RazonSocial)).ToList(),
                        Subcontratas.Select(s => new EmpleadorDeTrabajadorDto(s.Id, s.RazonSocial)).ToList());
                case ObtenerTrabajadoresQuery q:
                    return Filtrar(q);
                case ObtenerTrabajadorPorIdQuery q:
                    return Detalle(q.Id)!;
                case ObtenerCentrosParaSelectorQuery:
                    return (IReadOnlyList<CentroSelectorDto>)Centros.ToList();
                case ObtenerDocumentosFaltantesParaAsignacionQuery q:
                    return Faltantes(q.CentroIds.Single());
                case CrearTrabajadorCommand:
                    return Result.Exito(Guid.NewGuid());
                case GuardarFiltroCommand:
                    return ResultadoGuardarFiltro ?? Result.Exito(Guid.NewGuid());
                case EliminarFiltroGuardadoCommand e:
                    FiltrosGuardados.RemoveAll(f => f.Id == e.Id);
                    return Result.Exito();
                case EliminarTrabajadorCommand c:
                    _papelera.AddRange(Almacen.Where(f => f.Dto.Id == c.Id));
                    Almacen.RemoveAll(f => f.Dto.Id == c.Id);
                    return Result.Exito();
                case RestaurarTrabajadorCommand c:
                    Almacen.AddRange(_papelera.Where(f => f.Dto.Id == c.Id));
                    _papelera.RemoveAll(f => f.Dto.Id == c.Id);
                    return Result.Exito();
                case EliminarTrabajadoresCommand c:
                    {
                        var caidos = Almacen.Where(f => c.Ids.Contains(f.Dto.Id) && !NoEliminables.Contains(f.Dto.Id)).ToList();
                        _papelera.AddRange(caidos);
                        Almacen.RemoveAll(caidos.Contains);
                        var errores = c.Ids.Where(NoEliminables.Contains).Select(id => $"No se pudo borrar {id}.").ToList();
                        return Result.Exito(EliminadosForzados is { } forzados
                            ? new ResultadoEliminacionLoteDto(forzados, errores)
                            : new ResultadoEliminacionLoteDto(caidos.Count, errores, caidos.Select(f => f.Dto.Id).ToList()));
                    }
                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            }
        }

        public TrabajadorDetalleDto? Detalle(Guid id) =>
            Almacen.Select(f => f.Dto).Where(t => t.Id == id)
                .Select(t => new TrabajadorDetalleDto(t.Id, null, null, t.EmpleadorNombre, t.Nombre, t.Apellidos, t.Dni,
                    null, null, null, null, null, null, Guid.NewGuid()))
                .SingleOrDefault();

        /// <summary>
        /// Filtra, ordena y pagina como <c>ObtenerTrabajadoresQueryHandler</c>:
        /// el total es el de los coincidentes y las filas, solo las de la
        /// página pedida.
        /// </summary>
        public ResultadoPaginado<TrabajadorListaDto> Filtrar(ObtenerTrabajadoresQuery q)
        {
            var coincidentes = Almacen
                .Where(f => string.IsNullOrWhiteSpace(q.Busqueda) || CoincideBusqueda(f, q.Busqueda))
                .Where(f => q.EmpresaId is null || f.EmpresaId == q.EmpresaId)
                .Where(f => q.SubcontrataId is null || f.SubcontrataId == q.SubcontrataId)
                .Where(f => EstadoDocumentalFiltro.Coincide(f.Dto.EstadoDocumental, q.EstadoDocumental))
                .Select(f => f.Dto)
                .ToList();

            var pagina = Ordenar(coincidentes, q)
                .Skip((q.Pagina - 1) * q.TamanoPagina)
                .Take(q.TamanoPagina)
                .ToList();
            return new ResultadoPaginado<TrabajadorListaDto>(pagina, coincidentes.Count, q.Pagina, q.TamanoPagina);
        }

        /// <summary>
        /// Campo a campo, como el handler: «Javier Salas» no casa con nadie
        /// aunque sean el nombre y el apellido de la misma fila.
        /// </summary>
        private static bool CoincideBusqueda(Fila f, string busqueda) =>
            new[] { f.Dto.Nombre, f.Dto.Apellidos, f.Dto.Dni ?? string.Empty, f.Alias ?? string.Empty }
                .Any(campo => campo.Contains(busqueda, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Mismo reparto en dos caminos que el handler. Con filtro de
        /// documentación, o al ordenar por ella, el handler ordena por la clave
        /// del estado (<c>Descendente</c> la invierte) y después por apellidos,
        /// nombre e Id, SIN mirar qué columna pide <c>OrdenarPor</c>. Si no,
        /// lista blanca de columnas —cualquier otro nombre cae en apellidos,
        /// nombre— y el Id como desempate final.
        /// </summary>
        private static IEnumerable<TrabajadorListaDto> Ordenar(List<TrabajadorListaDto> filas, ObtenerTrabajadoresQuery q)
        {
            var c = StringComparer.Ordinal;

            var porEstado = !string.IsNullOrWhiteSpace(q.EstadoDocumental)
                || q.OrdenarPor == nameof(TrabajadorListaDto.EstadoDocumental);
            if (porEstado)
            {
                return (q.Descendente
                        ? filas.OrderByDescending(t => EstadoDocumentalFiltro.ClaveOrden(t.EstadoDocumental))
                        : filas.OrderBy(t => EstadoDocumentalFiltro.ClaveOrden(t.EstadoDocumental)))
                    .ThenBy(t => t.Apellidos, c).ThenBy(t => t.Nombre, c).ThenBy(t => t.Id);
            }

            IOrderedEnumerable<TrabajadorListaDto> ordenada = (q.OrdenarPor, q.Descendente) switch
            {
                (nameof(TrabajadorListaDto.Apellidos), true) => filas.OrderByDescending(t => t.Apellidos, c).ThenBy(t => t.Nombre, c),
                (nameof(TrabajadorListaDto.Nombre), false) => filas.OrderBy(t => t.Nombre, c).ThenBy(t => t.Apellidos, c),
                (nameof(TrabajadorListaDto.Nombre), true) => filas.OrderByDescending(t => t.Nombre, c).ThenBy(t => t.Apellidos, c),
                (nameof(TrabajadorListaDto.Dni), false) => filas.OrderBy(t => t.Dni, c),
                (nameof(TrabajadorListaDto.Dni), true) => filas.OrderByDescending(t => t.Dni, c),
                (nameof(TrabajadorListaDto.EmpleadorNombre), false) => filas.OrderBy(t => t.EmpleadorNombre, c).ThenBy(t => t.Apellidos, c),
                (nameof(TrabajadorListaDto.EmpleadorNombre), true) => filas.OrderByDescending(t => t.EmpleadorNombre, c).ThenBy(t => t.Apellidos, c),
                _ => filas.OrderBy(t => t.Apellidos, c).ThenBy(t => t.Nombre, c)
            };
            return ordenada.ThenBy(t => t.Id);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private static Fila Trabajador(string nombre, string apellidos, Guid? empresaId = null, Guid? subcontrataId = null,
        EstadoDocumento? estado = EstadoDocumento.Vigente, string? alias = null)
    {
        var empleador = subcontrataId is not null ? "Aislamientos Nervión S.L."
            : empresaId == EmpresaDexter ? "Dexter Industrial S.A." : "Montajes Ebro S.L.";
        return new Fila(
            new TrabajadorListaDto(Guid.NewGuid(), nombre, apellidos, $"{Math.Abs(apellidos.GetHashCode()) % 100000000:00000000}Z", empleador, estado),
            subcontrataId is null ? empresaId ?? EmpresaEbro : null,
            subcontrataId,
            alias);
    }

    private SeleccionEmpresaGestionadaDePrueba Seleccion = new();

    // --- Empresa gestionada activa (lote 2 del selector de Tenant beneficiario) ----------------

    private static readonly Guid Origen = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaNorte = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid EmpresaSur = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private static MediatorFalso ConCartera(bool origenGestionado)
    {
        var mediador = new MediatorFalso();
        mediador.Autorizados.Clear();
        mediador.Autorizados.AddRange(
        [
            new ClienteAutorizadoDto(Origen, "Operador de prueba", EsOrigen: true, EsGestionadoPorOperacion: origenGestionado),
            new ClienteAutorizadoDto(EmpresaNorte, "Empresa Norte", EsOrigen: false, EsGestionadoPorOperacion: true),
            new ClienteAutorizadoDto(EmpresaSur, "Empresa Sur", EsOrigen: false, EsGestionadoPorOperacion: true),
        ]);
        return mediador;
    }

    /// <summary>
    /// Rediseño de listados, fase 1: la lista ya no repite qué empresa gestionada está activa —lo
    /// dice el selector de la barra lateral—, pero sí es la de la elegida: se monta y pide sus datos.
    /// </summary>
    [Fact]
    public void Con_varias_empresas_gestionadas_la_lista_no_repite_el_rotulo_de_la_empresa_activa()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);

        var cut = Renderizar(mediador);

        ConsultasDeLista(mediador).Should().BeGreaterThan(0, "control positivo: con la empresa elegida la lista se pide");
        cut.FindAll(".barra-filtros-pastillas").Should().NotBeEmpty();
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.Markup.Should().NotContain("Empresa gestionada").And.NotContain("Empresa Sur");
    }

    [Fact]
    public void Un_usuario_mono_Tenant_no_ve_cabecera_de_empresa_gestionada()
    {
        var cut = Renderizar(new MediatorFalso());

        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.FindAll("table, [role=grid]").Should().NotBeEmpty("la lista se pinta como siempre");
    }

    [Fact]
    public void Sin_empresa_elegida_y_con_el_origen_sin_gestionar_pide_elegir_y_no_muestra_datos_del_origen()
    {
        var mediador = ConCartera(origenGestionado: false);

        var cut = Renderizar(mediador);

        cut.Markup.Should().Contain("Selecciona una empresa de tu cartera");
        cut.FindAll(".barra-filtros-pastillas").Should().BeEmpty();
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().BeEmpty("su «Exportar a Excel» exportaría los datos del origen");
        ConsultasDeLista(mediador).Should().Be(0, "no se piden los trabajadores de la organización de origen");
        mediador.Enviadas.OfType<ObtenerEmpresasParaSelectorQuery>().Should().BeEmpty("tampoco los catálogos del origen");
    }

    [Fact]
    public void Con_el_origen_gestionado_y_sin_empresa_elegida_la_lista_es_la_del_origen()
    {
        var mediador = ConCartera(origenGestionado: true);

        var cut = Renderizar(mediador);

        cut.Markup.Should().NotContain("Selecciona una empresa de tu cartera");
        cut.FindAll(".barra-filtros-pastillas").Should().NotBeEmpty("la lista del origen se monta");
    }

    [Fact]
    public void Mientras_se_resuelve_la_empresa_activa_no_se_monta_la_lista_ni_se_ofrece_la_exportacion()
    {
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;
        Registrar(mediador, "trabajadores");

        var cut = Render<Trabajadores>();

        cut.FindAll(".barra-filtros-pastillas").Should().BeEmpty();
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().BeEmpty("su «Exportar a Excel» exportaría los datos del origen");
        ConsultasDeLista(mediador).Should().Be(0);

        puerta.SetResult();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Selecciona una empresa de tu cartera"));
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().BeEmpty();
        ConsultasDeLista(mediador).Should().Be(0, "ni antes ni después de resolverse se pide la lista del origen");
    }

    /// <summary>
    /// En el estado 4a no hay empresa elegida: la cabecera no enseña el Tenant de origen como si lo fuera,
    /// encima del «Selecciona una empresa» (hueco medio preexistente: la página resolvía por su cuenta).
    /// </summary>
    [Fact]
    public void Sin_empresa_elegida_la_cabecera_de_empresa_activa_no_se_pinta()
    {
        var cut = Renderizar(ConCartera(origenGestionado: false));

        cut.Markup.Should().Contain("Selecciona una empresa de tu cartera", "control positivo: es el estado 4a");
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty("el origen no es la empresa elegida");
    }

    /// <summary>Un enlace de guardar filtro no abre el diálogo en el estado 4a: no hay lista del Tenant de origen.</summary>
    [Fact]
    public void Sin_empresa_elegida_la_accion_guardar_filtro_de_la_url_no_abre_el_dialogo()
    {
        var cut = Renderizar(ConCartera(origenGestionado: false), "trabajadores?accion=guardar-filtro");

        cut.Markup.Should().Contain("Selecciona una empresa de tu cartera", "control positivo: es el estado 4a");
        MostrarGuardarFiltro(cut.Instance).Should().BeFalse();
    }

    /// <summary>Control positivo del anterior: con una empresa elegida, la misma URL sí abre el diálogo.</summary>
    [Fact]
    public void Con_empresa_elegida_la_accion_guardar_filtro_de_la_url_abre_el_dialogo()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);

        var cut = Renderizar(ConCartera(origenGestionado: false), "trabajadores?accion=guardar-filtro");

        MostrarGuardarFiltro(cut.Instance).Should().BeTrue();
    }

    /// <summary>Salir de la página con la resolución en vuelo la cancela: no se repinta ni se pide la lista de nadie.</summary>
    [Fact]
    public async Task Salir_de_la_pagina_con_la_empresa_activa_en_vuelo_cancela_la_resolucion()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;
        Registrar(mediador, "trabajadores");

        var cut = Render<Trabajadores>();
        mediador.TokenDeAutorizados.Should().NotBeNull("la página pidió la lista de Tenants autorizados");
        mediador.TokenDeAutorizados!.Value.IsCancellationRequested.Should().BeFalse("control positivo: sigue montada");

        var pagina = cut.Instance;
        await DisposeComponentsAsync();

        mediador.TokenDeAutorizados!.Value.IsCancellationRequested.Should().BeTrue();
        puerta.SetResult();
        await EsperarFinDeResolucionAsync(pagina);
        ConsultasDeLista(mediador).Should().Be(0);
        mediador.Enviadas.OfType<ObtenerEmpresasParaSelectorQuery>().Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().BeEmpty();
    }

    /// <summary>
    /// Si la resolución vuelve sin lanzar tras retirarse la página (contexto sin empresa activa ni 4a), la
    /// página tampoco sigue: ni catálogos ni filtros guardados de una página que ya no existe.
    /// </summary>
    [Fact]
    public async Task Una_resolucion_que_vuelve_sin_lanzar_tras_retirar_la_pagina_no_pide_catalogos_ni_filtros_guardados()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);
        mediador.IgnorarCancelacionDeAutorizados = true;
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;
        Registrar(mediador, "trabajadores");

        var cut = Render<Trabajadores>();
        var pagina = cut.Instance;
        await DisposeComponentsAsync();
        puerta.SetResult();
        await EsperarFinDeResolucionAsync(pagina);

        ConsultasDeLista(mediador).Should().Be(0);
        mediador.Enviadas.OfType<ObtenerEmpresasParaSelectorQuery>().Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().BeEmpty();
        mediador.Enviadas.Should().OnlyContain(e => e is ObtenerClientesAutorizadosQuery, "tras retirarse solo consta la resolución que ya iba en vuelo");
    }

    /// <summary>
    /// Retirada la página con la resolución en vuelo, ComponentBase todavía invoca <c>OnParametersSetAsync</c>
    /// con <c>_resolviendoEmpresa</c> ya en false: con <c>?accion=guardar-filtro</c> abriría el diálogo en un
    /// componente muerto.
    /// </summary>
    [Fact]
    public async Task Retirada_la_pagina_con_la_resolucion_en_vuelo_no_se_procesan_los_parametros_de_la_url()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;
        Registrar(mediador, "trabajadores?accion=guardar-filtro");

        var cut = Render<Trabajadores>();
        var pagina = cut.Instance;
        await DisposeComponentsAsync();
        puerta.SetResult();
        await EsperarFinDeResolucionAsync(pagina);

        // El componente está retirado y no hay DOM que mirar: se lee el estado que la acción habría fijado.
        MostrarGuardarFiltro(pagina).Should().BeFalse("una página retirada no atiende ?accion=guardar-filtro");
    }

    /// <summary>
    /// Barrera positiva: las continuaciones de <c>OnInitializedAsync</c> y <c>OnParametersSetAsync</c> se
    /// publican en el contexto del renderer, así que un negativo evaluado justo tras <c>SetResult</c> podría
    /// pasar antes de que corran. Se espera a que la resolución termine y se vacía la cola del renderer.
    /// </summary>
    private static async Task EsperarFinDeResolucionAsync(Trabajadores pagina)
    {
        // Retirada del árbol, el componente ya no admite WaitForAssertion ni InvokeAsync: se sondea la instancia.
        var campo = typeof(Trabajadores).GetField("_resolviendoEmpresa", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var limite = DateTime.UtcNow.AddSeconds(5);
        while ((bool)campo.GetValue(pagina)! && DateTime.UtcNow < limite)
            await Task.Delay(10);

        ((bool)campo.GetValue(pagina)!).Should().BeFalse("la resolución en vuelo ya terminó");
        await Task.Delay(50); // deja correr lo que siga a la resolución (OnParametersSetAsync)
    }

    private static bool MostrarGuardarFiltro(Trabajadores pagina) =>
        (bool)typeof(Trabajadores)
            .GetField("_mostrarGuardarFiltro", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(pagina)!;

    [Fact]
    public void Con_el_contexto_resuelto_a_una_empresa_la_lista_se_monta_tras_la_carga()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;
        Registrar(mediador, "trabajadores");

        var cut = Render<Trabajadores>();
        cut.FindAll(".barra-filtros-pastillas").Should().BeEmpty();

        puerta.SetResult();

        cut.WaitForAssertion(() => cut.FindAll(".barra-filtros-pastillas").Should().NotBeEmpty());
    }

    private void Registrar(MediatorFalso mediador, string url)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ITenantActual>(_ => Seleccion);
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearTrabajadorCommand>>(_ => new InlineValidator<CrearTrabajadorCommand>());

        // Los filtros de URL son [SupplyParameterFromQuery]: se llega a ellos
        // navegando, no pasándolos como parámetros de componente.
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
    }

    private IRenderedComponent<Trabajadores> Renderizar(MediatorFalso mediador, string url = "trabajadores")
    {
        Registrar(mediador, url);
        var cut = Render<Trabajadores>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private static List<IElement> FilasDeDatos(IRenderedComponent<Trabajadores> cut) =>
        cut.FindAll("tbody tr").Where(tr => tr.QuerySelector(".enlace-nombre-fila") is not null).ToList();

    /// <summary>
    /// La parte <paramref name="columna"/> (0 Apellidos, 1 Nombre) de la celda «Trabajador» de cada
    /// fila, que dice «Apellidos, Nombre» en un solo botón (rediseño de listados, fase 1).
    /// </summary>
    private static List<string> Columna(IRenderedComponent<Trabajadores> cut, int columna) =>
        FilasDeDatos(cut).Select(tr => tr.QuerySelector(".enlace-nombre-fila")!.TextContent.Trim().Split(", ")[columna]).ToList();

    /// <summary>
    /// El disparador de la pastilla de filtro de ese nombre: su nombre accesible es la etiqueta sin
    /// valor aplicado, o «etiqueta: opción» con valor.
    /// </summary>
    private static IElement Pastilla(IRenderedComponent<Trabajadores> cut, string etiqueta) =>
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla")
            .Single(b => b.GetAttribute("aria-label") is { } nombre && (nombre == etiqueta || nombre.StartsWith(etiqueta + ": ", StringComparison.Ordinal)));

    /// <summary>Abre la pastilla y pulsa la opción (menuitemradio) con ese texto.</summary>
    private static async Task ElegirEnLaPastilla(IRenderedComponent<Trabajadores> cut, string etiqueta, string opcion)
    {
        await Pastilla(cut, etiqueta).ClickAsync(new MouseEventArgs());
        await cut.FindAll(".barra-filtros-pastillas [role=menuitemradio]")
            .Single(i => i.TextContent.Trim() == opcion).ClickAsync(new MouseEventArgs());
    }

    /// <summary>
    /// Marca o desmarca un botón de la franja de estado por su rótulo. La documentación ya no es una pastilla:
    /// «Vencidos», «Por vencer», «Sin confirmar» y «Sin incidencias» se marcan por separado y se suman.
    /// </summary>
    private static Task AlternarEnLaFranja(IRenderedComponent<Trabajadores> cut, string rotulo) =>
        cut.BotonDeFranja(rotulo).ClickAsync(new MouseEventArgs());

    /// <summary>Textos de las opciones de la pastilla, abierta para leerlos.</summary>
    private static async Task<List<string>> OpcionesDeLaPastilla(IRenderedComponent<Trabajadores> cut, string etiqueta)
    {
        await Pastilla(cut, etiqueta).ClickAsync(new MouseEventArgs());
        return cut.FindAll(".barra-filtros-pastillas [role=menuitemradio]").Select(i => i.TextContent.Trim()).ToList();
    }

    /// <summary>Abre «Más filtros» y pulsa el ítem con ese texto (un filtro guardado o «Guardar filtro»).</summary>
    private static async Task PulsarEnMasFiltros(IRenderedComponent<Trabajadores> cut, string item)
    {
        await Pastilla(cut, "Más filtros").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".barra-filtros-pastillas [role=menuitem]")
            .Single(i => i.TextContent.Trim() == item).ClickAsync(new MouseEventArgs());
    }

    /// <summary>El conmutador ☑ «Selección múltiple» de la cabecera.</summary>
    private static IElement ConmutadorSeleccion(IRenderedComponent<Trabajadores> cut) =>
        cut.Find("header.cabecera-pagina button.cabecera-listado-icono");

    private static Task AlternarSeleccionMultiple(IRenderedComponent<Trabajadores> cut) =>
        ConmutadorSeleccion(cut).ClickAsync(new MouseEventArgs());

    private static List<string> TextosDeLosChips(IRenderedComponent<Trabajadores> cut) =>
        cut.FindAll(".barra-filtros-pastillas .chip-filtro").Select(c => c.TextContent.Trim()).ToList();

    /// <summary>Clases de las filas con datos (QuickGrid rellena la página con filas vacías).</summary>
    private static List<string> ClasesDeLasFilas(IRenderedComponent<Trabajadores> cut) =>
        FilasDeDatos(cut).Select(tr => tr.ClassName ?? string.Empty).ToList();

    private static ObtenerTrabajadoresQuery UltimaConsulta(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Last();

    [Fact]
    public async Task Alta_correcta_actualiza_el_catalogo_visible_y_la_pastilla_sin_recargar_la_pagina()
    {
        var bea = Trabajador("Bea", "Alonso");
        var nueva = Trabajador("Carla", "Molina Ríos", empresaId: EmpresaDexter);
        var mediador = new MediatorFalso { Almacen = { bea }, EmpleadoresFiltro = CatalogoSoloEbroParaRefresh() };
        var cut = Renderizar(mediador);
        Columna(cut, 0).Should().Equal(["Alonso"], "control positivo: la lista inicial existe");
        await ComprobarOpcionesEmpresaYCerrarAsync(cut, "Todas", "Montajes Ebro S.L.");
        await AbrirAltaAsync(cut);
        await SelectorDeEmpleadorDelAlta(cut).Find("select").ChangeAsync(new ChangeEventArgs { Value = EmpresaDexter.ToString() });
        await EscribirDatosCompletosDelAltaAsync(cut);
        var consultasCatalogoAntes = ConsultasCatalogoVisibleParaRefresh(mediador);
        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().BeEmpty();

        // El fake cambia su estado al aceptar la escritura, igual que el consumidor; la respuesta
        // del catálogo ya está preparada antes del clic, para detectar si la página conserva su cache.
        mediador.EmpleadoresFiltro = CatalogoEbroDexterParaRefresh();
        mediador.Retener = p =>
        {
            if (p is not CrearTrabajadorCommand) return null;
            mediador.Almacen.Add(nueva);
            return Task.FromResult<object>(Result.Exito(nueva.Dto.Id));
        };
        await GuardarAlta(cut).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle();
        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Single().EmpresaId.Should().Be(EmpresaDexter);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().BeEquivalentTo("Alonso", "Molina Ríos"));
        await ComprobarCatalogoRefrescadoAsync(cut, mediador, consultasCatalogoAntes,
            "Todas", "Montajes Ebro S.L.", "Dexter Industrial S.A.");
    }

    [Fact]
    public async Task Baja_individual_y_Deshacer_actualizan_el_catalogo_visible_en_cada_exito()
    {
        var bea = Trabajador("Bea", "Alonso");
        var ana = Trabajador("Ana", "Moreno", empresaId: EmpresaDexter);
        var mediador = new MediatorFalso { Almacen = { bea, ana }, EmpleadoresFiltro = CatalogoEbroDexterParaRefresh() };
        var cut = Renderizar(mediador);
        Columna(cut, 0).Should().BeEquivalentTo("Alonso", "Moreno");
        await ComprobarOpcionesEmpresaYCerrarAsync(cut, "Todas", "Montajes Ebro S.L.", "Dexter Industrial S.A.");
        await PulsarEnElMenuDeLaFila(cut, 1, "Eliminar");
        mediador.Enviadas.OfType<EliminarTrabajadorCommand>().Should().BeEmpty("abrir el diálogo todavía no escribe");
        var consultasAntesDeBaja = ConsultasCatalogoVisibleParaRefresh(mediador);
        mediador.EmpleadoresFiltro = CatalogoSoloEbroParaRefresh();

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarTrabajadorCommand>().Should().Equal([new EliminarTrabajadorCommand(ana.Dto.Id)]);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Alonso"));
        await ComprobarCatalogoRefrescadoAsync(cut, mediador, consultasAntesDeBaja, "Todas", "Montajes Ebro S.L.");

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Single(m => m.TextoAccion == "Deshacer");
        aviso.OnAccion.Should().NotBeNull("control positivo: el deshacer es el callback real del aviso");
        var consultasAntesDeRestaurar = ConsultasCatalogoVisibleParaRefresh(mediador);
        mediador.EmpleadoresFiltro = CatalogoEbroDexterParaRefresh();
        await cut.InvokeAsync(aviso.OnAccion!);

        mediador.Enviadas.OfType<RestaurarTrabajadorCommand>().Should().Equal([new RestaurarTrabajadorCommand(ana.Dto.Id)]);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().BeEquivalentTo("Alonso", "Moreno"));
        await ComprobarCatalogoRefrescadoAsync(cut, mediador, consultasAntesDeRestaurar,
            "Todas", "Montajes Ebro S.L.", "Dexter Industrial S.A.");
    }

    [Fact]
    public async Task Baja_en_lote_y_Deshacer_del_lote_actualizan_empresas_y_subcontratas_visibles()
    {
        var bea = Trabajador("Bea", "Alonso");
        var ana = Trabajador("Ana", "Moreno", empresaId: EmpresaDexter);
        var carla = Trabajador("Carla", "Molina Ríos", subcontrataId: SubcontrataNervion);
        var catalogoCompleto = new EmpleadoresDeTrabajadoresDto(
            CatalogoEbroDexterParaRefresh().Empresas,
            [new EmpleadorDeTrabajadorDto(SubcontrataNervion, "Aislamientos Nervión S.L.")]);
        var mediador = new MediatorFalso { Almacen = { bea, ana, carla }, EmpleadoresFiltro = catalogoCompleto };
        var cut = Renderizar(mediador);
        Columna(cut, 0).Should().BeEquivalentTo("Alonso", "Moreno", "Molina Ríos");
        await ComprobarOpcionesEmpresaYCerrarAsync(cut, "Todas", "Montajes Ebro S.L.", "Dexter Industrial S.A.", "Aislamientos Nervión S.L. (subcontrata)");
        await AlternarSeleccionMultiple(cut);
        foreach (var fila in new[] { ana, carla })
            await cut.Find($"tbody input[aria-label='Seleccionar a {fila.Dto.Nombre} {fila.Dto.Apellidos}']")
                .ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados").ClickAsync(new MouseEventArgs());
        mediador.Enviadas.OfType<EliminarTrabajadoresCommand>().Should().BeEmpty();
        var consultasAntesDeBaja = ConsultasCatalogoVisibleParaRefresh(mediador);
        mediador.EmpleadoresFiltro = CatalogoSoloEbroParaRefresh();
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarTrabajadoresCommand>().Should().ContainSingle();
        mediador.Enviadas.OfType<EliminarTrabajadoresCommand>().Single().Ids.Should().BeEquivalentTo([ana.Dto.Id, carla.Dto.Id]);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Alonso"));
        await ComprobarCatalogoRefrescadoAsync(cut, mediador, consultasAntesDeBaja, "Todas", "Montajes Ebro S.L.");

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Single(m => m.TextoAccion == "Deshacer");
        var consultasAntesDeRestaurar = ConsultasCatalogoVisibleParaRefresh(mediador);
        mediador.EmpleadoresFiltro = catalogoCompleto;
        await cut.InvokeAsync(aviso.OnAccion!);

        mediador.Enviadas.OfType<RestaurarTrabajadorCommand>().Select(c => c.Id).Should().BeEquivalentTo([ana.Dto.Id, carla.Dto.Id]);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().BeEquivalentTo("Alonso", "Moreno", "Molina Ríos"));
        await ComprobarCatalogoRefrescadoAsync(cut, mediador, consultasAntesDeRestaurar,
            "Todas", "Montajes Ebro S.L.", "Dexter Industrial S.A.", "Aislamientos Nervión S.L. (subcontrata)");
    }

    [Fact]
    public async Task Asignar_a_centro_actualiza_el_catalogo_visible_despues_del_comando_correcto()
    {
        var bea = Trabajador("Bea", "Alonso");
        var centro = new CentroSelectorDto(Guid.NewGuid(), "Planta Zaragoza", "Cliente empresarial de prueba", "Montajes Ebro S.L.");
        var mediador = new MediatorFalso
        {
            Almacen = { bea },
            Centros = { centro },
            EmpleadoresFiltro = CatalogoSoloEbroParaRefresh(),
            // La fixture no responde esta orden de serie; Retener registra primero y responde éxito.
            Retener = p => p is CrearAsignacionesCommand
                ? Task.FromResult<object>(Result.Exito(new ResultadoAsignacionLoteDto(1, 0, 0, []))) : null,
        };
        var cut = Renderizar(mediador);
        Columna(cut, 0).Should().Equal("Alonso");
        await ComprobarOpcionesEmpresaYCerrarAsync(cut, "Todas", "Montajes Ebro S.L.");
        await AbrirAsignarACentro(cut, bea);
        await cut.InvokeAsync(() => cut.FindComponent<CampoBuscarSelect>().Instance.ValorChanged.InvokeAsync(centro.Id.ToString()));
        mediador.Enviadas.OfType<ObtenerDocumentosFaltantesParaAsignacionQuery>().Should().ContainSingle();
        mediador.Enviadas.OfType<CrearAsignacionesCommand>().Should().BeEmpty();
        var consultasCatalogoAntes = ConsultasCatalogoVisibleParaRefresh(mediador);
        var consultasListaAntes = ConsultasDeLista(mediador);
        // Respuesta distinta con el mismo ID: instrumenta una nueva lectura sin fingir que asignar
        // modifica el empleador del Trabajador. La derivación real de visibilidad se prueba en Integration.
        mediador.EmpleadoresFiltro = new EmpleadoresDeTrabajadoresDto(
            [new EmpleadorDeTrabajadorDto(EmpresaEbro, "Montajes Ebro S.L. actualizado")], []);

        await cut.FindAll("[role=dialog] .modal-pie button").Single(b => b.TextContent.Trim() == "Asignar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearAsignacionesCommand>().Should().ContainSingle();
        var enviada = mediador.Enviadas.OfType<CrearAsignacionesCommand>().Single();
        enviada.TrabajadorIds.Should().Equal(bea.Dto.Id);
        enviada.CentroIds.Should().Equal(centro.Id);
        cut.WaitForAssertion(() => ConsultasDeLista(mediador).Should().BeGreaterThan(consultasListaAntes));
        await ComprobarCatalogoRefrescadoAsync(cut, mediador, consultasCatalogoAntes,
            "Todas", "Montajes Ebro S.L. actualizado");
    }

    [Fact]
    public async Task Una_busqueda_de_lectura_no_vuelve_a_pedir_el_catalogo_visible()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Bea", "Alonso"), Trabajador("Ana", "Moreno") },
            EmpleadoresFiltro = CatalogoSoloEbroParaRefresh(),
        };
        var cut = Renderizar(mediador);
        Columna(cut, 0).Should().BeEquivalentTo("Alonso", "Moreno");
        var consultasCatalogoAntes = ConsultasCatalogoVisibleParaRefresh(mediador);
        consultasCatalogoAntes.Should().BeGreaterThan(0, "control positivo: el catálogo se pidió al inicializar");
        var consultasListaAntes = ConsultasDeLista(mediador);

        await CajaDeBusqueda(cut).Find("input").InputAsync(new ChangeEventArgs { Value = "Moreno" });

        cut.WaitForAssertion(() => UltimaConsulta(mediador).Busqueda.Should().Be("Moreno"));
        ConsultasDeLista(mediador).Should().BeGreaterThan(consultasListaAntes);
        ConsultasCatalogoVisibleParaRefresh(mediador).Should().Be(consultasCatalogoAntes,
            "el refresco del catálogo pertenece al éxito de escritura, no a RecargarAsync de lectura");
        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Si_falla_el_catalogo_tras_una_baja_la_lista_y_los_filtros_se_conservan_y_la_pastilla_solo_ofrece_Todas()
    {
        var bea = Trabajador("Bea", "Alonso");
        var ana = Trabajador("Ana", "Moreno");
        var mediador = new MediatorFalso { Almacen = { bea, ana }, EmpleadoresFiltro = CatalogoSoloEbroParaRefresh() };
        var cut = Renderizar(mediador);
        await ElegirEnLaPastilla(cut, "Empresa", "Montajes Ebro S.L.");
        UltimaConsulta(mediador).EmpresaId.Should().Be(EmpresaEbro, "control positivo: la lista tiene filtro de empleador");
        Columna(cut, 0).Should().BeEquivalentTo("Alonso", "Moreno");
        await ComprobarOpcionesEmpresaYCerrarAsync(cut, "Todas", "Montajes Ebro S.L.");
        await PulsarEnElMenuDeLaFila(cut, 0, "Eliminar");
        var consultasCatalogoAntes = ConsultasCatalogoVisibleParaRefresh(mediador);
        mediador.FallarEmpleadoresFiltro = true;

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarTrabajadorCommand>().Should().Equal([new EliminarTrabajadorCommand(bea.Dto.Id)]);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Moreno"));
        UltimaConsulta(mediador).EmpresaId.Should().Be(EmpresaEbro,
            "el fallo del catálogo auxiliar no puede soltar el filtro de la lista");
        cut.Markup.Should().NotContain("No pudimos cargar los trabajadores");
        await ComprobarCatalogoRefrescadoAsync(cut, mediador, consultasCatalogoAntes, "Todas");
    }

    /// <summary>
    /// Dos avisos distintos permiten dos restauraciones concurrentes. La respuesta o fallo del
    /// primer refresco llega después de la segunda respuesta: no puede reemplazar el catálogo vigente.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dos_Deshacer_individuales_no_dejan_que_la_respuesta_o_fallo_del_catalogo_anterior_pise_al_vigente(
        bool fallaElPrimero)
    {
        var bea = Trabajador("Bea", "Alonso");
        var ana = Trabajador("Ana", "Moreno", empresaId: EmpresaDexter);
        var carla = Trabajador("Carla", "Molina Ríos", subcontrataId: SubcontrataNervion);
        var completo = new EmpleadoresDeTrabajadoresDto(CatalogoEbroDexterParaRefresh().Empresas,
            [new EmpleadorDeTrabajadorDto(SubcontrataNervion, "Aislamientos Nervión S.L.")]);
        var mediador = new MediatorFalso { Almacen = { bea, ana, carla }, EmpleadoresFiltro = completo };
        var cut = Renderizar(mediador);
        Columna(cut, 0).Should().BeEquivalentTo("Alonso", "Moreno", "Molina Ríos");
        await ComprobarOpcionesEmpresaYCerrarAsync(cut, "Todas", "Montajes Ebro S.L.", "Dexter Industrial S.A.", "Aislamientos Nervión S.L. (subcontrata)");

        // Localizar cada fila por su botón evita depender del orden de los dos apellidos.
        var indiceAna = FilasDeDatos(cut).FindIndex(f => f.QuerySelector(".enlace-nombre-fila")!.TextContent.Trim() == "Moreno, Ana");
        indiceAna.Should().BeGreaterThanOrEqualTo(0);
        mediador.EmpleadoresFiltro = new EmpleadoresDeTrabajadoresDto(CatalogoSoloEbroParaRefresh().Empresas, completo.Subcontratas);
        await PulsarEnElMenuDeLaFila(cut, indiceAna, "Eliminar");
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Columna(cut, 0).Should().BeEquivalentTo("Alonso", "Molina Ríos"));
        var indiceCarla = FilasDeDatos(cut).FindIndex(f => f.QuerySelector(".enlace-nombre-fila")!.TextContent.Trim() == "Molina Ríos, Carla");
        indiceCarla.Should().BeGreaterThanOrEqualTo(0);
        mediador.EmpleadoresFiltro = CatalogoSoloEbroParaRefresh();
        await PulsarEnElMenuDeLaFila(cut, indiceCarla, "Eliminar");
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Alonso"));
        var avisos = Services.GetRequiredService<ToastService>().Mensajes.Where(m => m.TextoAccion == "Deshacer").ToList();
        avisos.Should().HaveCount(2, "hay dos callbacks reales de bajas diferentes");
        mediador.Enviadas.OfType<EliminarTrabajadorCommand>().Select(c => c.Id).Should().BeEquivalentTo([ana.Dto.Id, carla.Dto.Id]);

        var primera = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var segunda = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consultasRetenidas = 0;
        var consultasCatalogoAntes = ConsultasCatalogoVisibleParaRefresh(mediador);
        mediador.Retener = p => p is ObtenerEmpleadoresDeTrabajadoresVisiblesQuery
            ? (++consultasRetenidas == 1 ? primera.Task : segunda.Task) : null;

        // Sin await: el callback queda esperando la Query del catálogo.
        var deshacerPrimero = cut.InvokeAsync(avisos[0].OnAccion!);
        cut.WaitForAssertion(() => consultasRetenidas.Should().Be(1));
        var deshacerSegundo = cut.InvokeAsync(avisos[1].OnAccion!);
        cut.WaitForAssertion(() => consultasRetenidas.Should().Be(2));
        mediador.Enviadas.OfType<RestaurarTrabajadorCommand>().Select(c => c.Id).Should().BeEquivalentTo([ana.Dto.Id, carla.Dto.Id]);

        await cut.InvokeAsync(() => segunda.SetResult(completo));
        await deshacerSegundo;
        cut.WaitForAssertion(() => Columna(cut, 0).Should().BeEquivalentTo("Alonso", "Moreno", "Molina Ríos"));
        await ComprobarCatalogoRefrescadoAsync(cut, mediador, consultasCatalogoAntes,
            "Todas", "Montajes Ebro S.L.", "Dexter Industrial S.A.", "Aislamientos Nervión S.L. (subcontrata)");

        if (fallaElPrimero)
            await cut.InvokeAsync(() => primera.SetException(new InvalidOperationException("Fallo antiguo del catálogo.")));
        else
            await cut.InvokeAsync(() => primera.SetResult(CatalogoEbroDexterParaRefresh()));
        await deshacerPrimero;

        await ComprobarOpcionesEmpresaYCerrarAsync(cut,
            "Todas", "Montajes Ebro S.L.", "Dexter Industrial S.A.", "Aislamientos Nervión S.L. (subcontrata)");
        Columna(cut, 0).Should().BeEquivalentTo("Alonso", "Moreno", "Molina Ríos");
        consultasRetenidas.Should().Be(2, "control positivo: se resolvieron precisamente las dos preguntas retenidas");
    }

    private static EmpleadoresDeTrabajadoresDto CatalogoSoloEbroParaRefresh() =>
        new([new EmpleadorDeTrabajadorDto(EmpresaEbro, "Montajes Ebro S.L.")], []);

    private static EmpleadoresDeTrabajadoresDto CatalogoEbroDexterParaRefresh() =>
        new([new EmpleadorDeTrabajadorDto(EmpresaEbro, "Montajes Ebro S.L."),
             new EmpleadorDeTrabajadorDto(EmpresaDexter, "Dexter Industrial S.A.")], []);

    private static int ConsultasCatalogoVisibleParaRefresh(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerEmpleadoresDeTrabajadoresVisiblesQuery>().Count();

    private static async Task ComprobarOpcionesEmpresaYCerrarAsync(IRenderedComponent<Trabajadores> cut, params string[] opciones)
    {
        (await OpcionesDeLaPastilla(cut, "Empresa")).Should().Equal(opciones);
        // El helper existente deja la pastilla abierta; cerrarla conserva el punto de partida del siguiente gesto.
        await Pastilla(cut, "Empresa").ClickAsync(new MouseEventArgs());
    }

    private static async Task ComprobarCatalogoRefrescadoAsync(IRenderedComponent<Trabajadores> cut, MediatorFalso mediador,
        int consultasAntes, params string[] opciones)
    {
        cut.WaitForAssertion(() => ConsultasCatalogoVisibleParaRefresh(mediador).Should().BeGreaterThan(consultasAntes,
            "una escritura correcta vuelve a pedir los empleadores visibles"));
        await ComprobarOpcionesEmpresaYCerrarAsync(cut, opciones);
    }

    private static int ConsultasDeLista(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Count();

    private static IRenderedComponent<CampoTexto> CajaDeBusqueda(IRenderedComponent<Trabajadores> cut) =>
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Placeholder?.StartsWith("Filtrar esta pantalla", StringComparison.Ordinal) == true);

    /// <summary>Cuenta las navegaciones desde ahora: cada una es una pasada de parámetros más.</summary>
    private Func<int> ContarNavegaciones()
    {
        var navegaciones = 0;
        Services.GetRequiredService<NavigationManager>().LocationChanged += (_, _) => navegaciones++;
        return () => navegaciones;
    }

    // --- Cabecera y barra de filtros (rediseño de listados, fase 1) ----------------------------

    /// <summary>
    /// Cabecera de una línea: título del perfil con el contador, ☑ «Selección múltiple», el «⋯» y
    /// UNA primaria; sin antetítulo y sin el rótulo de la empresa gestionada.
    /// </summary>
    [Theory]
    [InlineData(PerfilVocabularioTenant.Consultora, true, "Trabajadores")]
    [InlineData(PerfilVocabularioTenant.ClienteDirecto, true, "Mis trabajadores")]
    // Decisión del propietario 2026-09-28: usuario de otro Tenant en un Tenant Cliente Directo.
    [InlineData(PerfilVocabularioTenant.ClienteDirecto, false, "Trabajadores")]
    public void La_cabecera_es_de_una_linea_con_el_titulo_del_perfil_contador_seleccion_menu_y_una_primaria(
        PerfilVocabularioTenant perfil, bool esDelPropioTenant, string titulo)
    {
        var mediador = new MediatorFalso
        {
            Perfil = perfil,
            EsDelPropioTenant = esDelPropioTenant,
            Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz") }
        };
        var cut = Renderizar(mediador);

        var cabecera = cut.Find("header.cabecera-pagina");
        cabecera.QuerySelector("h1.titulo-pagina")!.TextContent.Trim().Should().Be(titulo);
        cabecera.QuerySelector(".cabecera-pagina-kicker").Should().BeNull("el grupo del menú ya está en las migas");
        cabecera.QuerySelector(".cabecera-listado-contador")!.TextContent.Trim().Should().Be("2");
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();

        var acciones = cabecera.QuerySelector(".acciones-cabecera")!;
        acciones.QuerySelectorAll("a").Should().BeEmpty("«Exportar a Excel» vive ahora dentro del «⋯»");
        acciones.QuerySelectorAll("button").Select(b => b.GetAttribute("aria-label") ?? b.TextContent.Trim())
            .Should().Equal("Selección múltiple", "Atajos de teclado", "Más acciones", "+ Nuevo trabajador");
    }

    [Fact]
    public async Task El_menu_de_la_cabecera_exporta_a_Excel_con_un_enlace_de_descarga()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno") } });

        await cut.Find("header.cabecera-pagina .menu-acciones-disparador").ClickAsync(new MouseEventArgs());

        var items = cut.FindAll("header.cabecera-pagina .menu-acciones-item");
        items.Select(i => i.TextContent.Trim()).Should().Equal("Exportar esta vista (filas: 1)", "Exportar todo");
        items.Should().OnlyContain(i => i.TagName == "A", "la exportación es una descarga del servidor: un enlace, no una navegación interna");
        items[0].GetAttribute("href").Should().StartWith("/trabajadores/exportar.xlsx");
        items[1].GetAttribute("href").Should().Be("/trabajadores/exportar.xlsx", "«Exportar todo» va sin criterios");
    }

    [Fact]
    public async Task Nuevo_trabajador_de_la_cabecera_abre_el_drawer_de_alta()
    {
        var cut = Renderizar(new MediatorFalso());
        cut.FindAll(".drawer-panel").Should().BeEmpty("punto de partida: el drawer está cerrado");

        await AbrirAltaAsync(cut);

        cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nuevo trabajador");
    }

    /// <summary>
    /// El buscador es el compartido «Filtrar esta pantalla» (la tecla f lo enfoca por
    /// data-filtro-pantalla) y promete lo que ObtenerTrabajadoresQuery busca: nombre (y apellidos),
    /// DNI y alias. Los E2E lo localizan por este marcador.
    /// </summary>
    [Fact]
    public void El_buscador_es_Filtrar_esta_pantalla_por_nombre_DNI_o_alias()
    {
        var cut = Renderizar(new MediatorFalso());

        var buscador = cut.Find(".barra-filtros-pastillas input[type=text]");
        buscador.GetAttribute("placeholder").Should().Be("Filtrar esta pantalla: nombre, DNI o alias");
        buscador.GetAttribute("aria-label").Should().Be("Filtrar esta pantalla");
        buscador.HasAttribute("data-filtro-pantalla").Should().BeTrue("es lo que enfoca la tecla f (atajos-lista.js)");
        cut.Find(".barra-filtros-pastillas kbd.barra-filtros-tecla").TextContent.Trim().Should().Be("F");
    }

    /// <summary>
    /// Pastillas «Documentación» y «Empresa», y «Más filtros». La maqueta dibuja también «Centro» y
    /// «Cliente empresarial» como fase 2: ObtenerTrabajadoresQuery no sabe filtrar por ellos, y una
    /// pastilla que no filtra nada no se pinta. Subcontrata no tiene pastilla propia: va dentro de
    /// «Empresa».
    /// </summary>
    [Fact]
    public void La_pastilla_es_Empresa_seguida_de_Mas_filtros_y_la_documentacion_va_en_la_franja()
    {
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno") } };
        var cut = Renderizar(mediador);

        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla").Select(b => b.GetAttribute("aria-label"))
            .Should().Equal("Empresa", "Más filtros");
        cut.RotulosDeFranja().Should().Equal("Todos", "Vencidos", "Por vencer", "Sin confirmar", "Sin incidencias");
        cut.MarcadosEnFranja().Should().Equal(["Todos"], "sin filtro de estado, el marcado es «Todos»");
        UltimaConsulta(mediador).ConRecuentosPorEstado.Should().BeTrue("sin pedirlos, la franja no tendría cifras");
        cut.Markup.Should().NotContain("Todos los centros");
    }

    /// <summary>
    /// La franja admite varios estados: cada botón marcado se suma en la consulta y en la URL
    /// (<c>estado=Vencido,Urgente,Proximo</c>), «Por vencer» manda Urgente y Próximo a la vez, desmarcar quita
    /// solo lo suyo y «Todos» borra la selección.
    /// </summary>
    [Fact]
    public async Task La_franja_suma_los_estados_marcados_en_la_consulta_y_en_la_url_y_Todos_los_borra()
    {
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Vencido),
                Trabajador("Ana", "Vega Ortiz", estado: EstadoDocumento.Proximo),
                Trabajador("Luis", "Iglesias Rey", estado: EstadoDocumento.Vigente)
            }
        };
        var cut = Renderizar(mediador);
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await AlternarEnLaFranja(cut, "Vencidos");
        await AlternarEnLaFranja(cut, "Por vencer");

        UltimaConsulta(mediador).EstadoDocumental.Should().Be("Vencido,Urgente,Proximo");
        Uri.UnescapeDataString(navegacion.Uri).Should().EndWith("estado=Vencido,Urgente,Proximo");
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Salas Moreno", "Vega Ortiz"));
        cut.MarcadosEnFranja().Should().Equal("Vencidos", "Por vencer");
        TextosDeLosChips(cut).Should().BeEmpty("el estado se ve en la franja, no como chip");

        await AlternarEnLaFranja(cut, "Vencidos");

        UltimaConsulta(mediador).EstadoDocumental.Should().Be("Urgente,Proximo");
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Vega Ortiz"));
        cut.MarcadosEnFranja().Should().Equal("Por vencer");

        await AlternarEnLaFranja(cut, "Todos");

        UltimaConsulta(mediador).EstadoDocumental.Should().BeNull();
        navegacion.Uri.Should().NotContain("estado=");
        cut.WaitForAssertion(() => Columna(cut, 0).Should().HaveCount(3));
        cut.MarcadosEnFranja().Should().Equal("Todos");
    }

    [Fact]
    public async Task La_pastilla_Empresa_ofrece_las_empresas_y_detras_las_subcontratas_marcadas()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno") } });

        (await OpcionesDeLaPastilla(cut, "Empresa")).Should().Equal(
            "Todas", "Montajes Ebro S.L.", "Dexter Industrial S.A.", "Aislamientos Nervión S.L. (subcontrata)");
    }

    /// <summary>
    /// La visibilidad del empleador en el filtro no autoriza prellenar el alta: el Id debe pertenecer
    /// al selector recién cargado, también cuando procede de un filtro guardado obsoleto.
    /// </summary>
    [Theory]
    [InlineData(false, false)] // Empresa, pastilla
    [InlineData(true, false)]  // Subcontrata legacy, pastilla
    [InlineData(false, true)]  // Empresa, filtro guardado obsoleto para el alta
    [InlineData(true, true)]   // Subcontrata legacy, filtro guardado obsoleto para el alta
    public async Task Alta_no_prellena_el_empleador_del_filtro_ausente_del_selector_y_no_envia_hasta_elegir_uno_disponible(
        bool esSubcontrata, bool filtroGuardado)
    {
        var (mediador, idFiltro, idDisponible, opcionFiltro) = PrepararCatalogosDeAlta(esSubcontrata, autorizado: false);
        var cut = await AbrirConFiltroDeEmpleadorAsync(mediador, esSubcontrata, filtroGuardado, idFiltro, opcionFiltro);
        var consulta = UltimaConsulta(mediador);
        (esSubcontrata ? consulta.SubcontrataId : consulta.EmpresaId).Should().Be(idFiltro,
            "control positivo: la lista conserva su filtro de visibilidad");

        await AbrirAltaAsync(cut);

        SelectorDeEmpleadorDelAlta(cut).Instance.Valor.Should().BeEmpty(
            "el ID visible para la lista está ausente del selector de alta y no puede prellenarse");
        SelectorDeEmpleadorDelAlta(cut).FindAll("option")
            .Should().NotContain(o => o.GetAttribute("value") == idFiltro.ToString(),
                "control positivo: el selector de alta realmente excluye el ID del filtro");
        await EscribirDatosCompletosDelAltaAsync(cut);
        await GuardarAlta(cut).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().BeEmpty(
            "la barrera del drawer debe impedir enviar el ID excluido al productor Application");
        cut.FindAll(".drawer-panel").Should().ContainSingle("el alta incompleta sigue abierta");
        cut.Find(".drawer-panel").TextContent.Should().Contain("Selecciona una",
            "el impedimento observado es la falta de empleador, no otro campo");

        // La barrera puede abrir por defecto en Empresa o conservar el tipo del filtro sin ID.
        // Elegir explícitamente el tipo requerido hace el caso independiente de esa presentación.
        await cut.FindAll(".drawer-panel input[name='tipo-empleador']")[esSubcontrata ? 1 : 0]
            .ChangeAsync(new ChangeEventArgs { Value = true });
        await SelectorDeEmpleadorDelAlta(cut).Find("select")
            .ChangeAsync(new ChangeEventArgs { Value = idDisponible.ToString() });
        await GuardarAlta(cut).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle();
        var enviada = mediador.Enviadas.OfType<CrearTrabajadorCommand>().Single();
        enviada.EmpresaId.Should().Be(esSubcontrata ? null : idDisponible);
        enviada.SubcontrataId.Should().Be(esSubcontrata ? idDisponible : null);
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().BeEmpty());
    }

    /// <summary>Control positivo: un ID del filtro que sigue disponible para el alta conserva el prellenado.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Alta_conserva_el_empleador_del_filtro_si_pertenece_al_selector_de_alta(
        bool esSubcontrata, bool filtroGuardado)
    {
        var (mediador, idFiltro, _, opcionFiltro) = PrepararCatalogosDeAlta(esSubcontrata, autorizado: true);
        var cut = await AbrirConFiltroDeEmpleadorAsync(mediador, esSubcontrata, filtroGuardado, idFiltro, opcionFiltro);
        await AbrirAltaAsync(cut);

        var selector = SelectorDeEmpleadorDelAlta(cut);
        selector.Instance.Etiqueta.Should().Be(esSubcontrata ? "Subcontrata" : "Empresa");
        selector.Instance.Valor.Should().Be(idFiltro.ToString());
        selector.FindAll("option").Should().Contain(o => o.GetAttribute("value") == idFiltro.ToString());
        await EscribirDatosCompletosDelAltaAsync(cut);
        await GuardarAlta(cut).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle();
        var enviada = mediador.Enviadas.OfType<CrearTrabajadorCommand>().Single();
        enviada.EmpresaId.Should().Be(esSubcontrata ? null : idFiltro);
        enviada.SubcontrataId.Should().Be(esSubcontrata ? idFiltro : null);
    }

    private static (MediatorFalso Mediador, Guid IdFiltro, Guid IdDisponible, string OpcionFiltro)
        PrepararCatalogosDeAlta(bool esSubcontrata, bool autorizado)
    {
        var idFiltro = esSubcontrata ? SubcontrataNervion : EmpresaEbro;
        var idDisponible = autorizado ? idFiltro
            : esSubcontrata ? Guid.Parse("8b7b85e7-8265-494c-9d50-7bb11312f302") : EmpresaDexter;
        var mediador = new MediatorFalso
        {
            // Consultora evita que una Empresa única se resuelva en silencio: aquí se mide el prellenado por filtro.
            Perfil = PerfilVocabularioTenant.Consultora,
            EmpleadoresFiltro = new EmpleadoresDeTrabajadoresDto(
                esSubcontrata ? [] : [new EmpleadorDeTrabajadorDto(idFiltro, "Montajes Ebro S.L.")],
                esSubcontrata ? [new EmpleadorDeTrabajadorDto(idFiltro, "Aislamientos Nervión S.L.")] : []),
        };
        mediador.Almacen.Add(esSubcontrata
            ? Trabajador("Javier", "Salas Moreno", subcontrataId: idFiltro)
            : Trabajador("Javier", "Salas Moreno", empresaId: idFiltro));
        mediador.Empresas.Clear();
        mediador.Empresas.Add(new EmpresaSelectorDto(esSubcontrata ? EmpresaDexter : idDisponible, "Empresa disponible para el alta"));
        mediador.Subcontratas.Clear();
        mediador.Subcontratas.Add(new SubcontrataSelectorDto(esSubcontrata ? idDisponible : SubcontrataNervion, "Empleador disponible para el alta"));
        return (mediador, idFiltro, idDisponible,
            esSubcontrata ? "Aislamientos Nervión S.L. (subcontrata)" : "Montajes Ebro S.L.");
    }

    private async Task<IRenderedComponent<Trabajadores>> AbrirConFiltroDeEmpleadorAsync(
        MediatorFalso mediador, bool esSubcontrata, bool filtroGuardado, Guid idFiltro, string opcionFiltro)
    {
        if (filtroGuardado)
        {
            var valoresJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                EmpresaId = esSubcontrata ? null : idFiltro.ToString(),
                SubcontrataId = esSubcontrata ? idFiltro.ToString() : null,
            });
            mediador.FiltrosGuardados.Add(new FiltroGuardadoDto(Guid.NewGuid(), "Empleador guardado", valoresJson, DateTime.UtcNow));
        }
        var cut = Renderizar(mediador);
        if (filtroGuardado)
            await PulsarEnMasFiltros(cut, "Empleador guardado");
        else
            await ElegirEnLaPastilla(cut, "Empresa", opcionFiltro);
        return cut;
    }

    private static IRenderedComponent<CampoSelect> SelectorDeEmpleadorDelAlta(IRenderedComponent<Trabajadores> cut) =>
        cut.FindComponents<CampoSelect>().Single(c => c.Instance.Etiqueta is "Empresa" or "Subcontrata");

    private static IElement GuardarAlta(IRenderedComponent<Trabajadores> cut) =>
        cut.Find(".drawer-panel").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Guardar");

    private static async Task EscribirDatosCompletosDelAltaAsync(IRenderedComponent<Trabajadores> cut)
    {
        await EscribirDocumentoAsync(cut, "60005002A");
        await cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre")
            .Find("input").InputAsync(new ChangeEventArgs { Value = "Carla" });
        await cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Apellidos")
            .Find("input").InputAsync(new ChangeEventArgs { Value = "Molina Ríos" });
    }

    [Fact]
    public async Task La_pastilla_Empresa_ofrece_los_empleadores_de_los_trabajadores_visibles_y_no_los_selectores_del_alta()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Javier", "Salas Moreno") },
            EmpleadoresFiltro = new EmpleadoresDeTrabajadoresDto([new EmpleadorDeTrabajadorDto(EmpresaEbro, "Montajes Ebro S.L.")], []),
        };
        mediador.Empresas.Clear();

        var cut = Renderizar(mediador);

        (await OpcionesDeLaPastilla(cut, "Empresa")).Should().Equal("Todas", "Montajes Ebro S.L.");
        mediador.Enviadas.OfType<ObtenerSubcontratasParaSelectorQuery>().Should().BeEmpty("el catálogo de Subcontratas no se pide para filtrar");
    }

    /// <summary>Si fallan las opciones de la pastilla «Empresa», la lista se pinta igual y la pastilla solo ofrece «Todas».</summary>
    [Fact]
    public async Task Si_fallan_las_opciones_de_Empresa_la_lista_se_ve_igual()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno") }, FallarEmpleadoresFiltro = true });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Salas Moreno"));
        (await OpcionesDeLaPastilla(cut, "Empresa")).Should().Equal("Todas");
    }

    /// <summary>
    /// Elegir «Todas» en la pastilla «Empresa» suelta el filtro que hubiera, sea de empresa o de
    /// subcontrata, en una sola consulta.
    /// </summary>
    [Fact]
    public async Task Todas_en_la_pastilla_Empresa_suelta_la_subcontrata_en_una_sola_consulta()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Marta", "Duarte Gil", subcontrataId: SubcontrataNervion) }
        };
        var cut = Renderizar(mediador);
        await ElegirEnLaPastilla(cut, "Empresa", "Aislamientos Nervión S.L. (subcontrata)");
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Duarte Gil"));
        Pastilla(cut, "Empresa").GetAttribute("aria-label").Should().Be("Empresa: Aislamientos Nervión S.L. (subcontrata)");
        var consultasAntes = ConsultasDeLista(mediador);

        await ElegirEnLaPastilla(cut, "Empresa", "Todas");

        UltimaConsulta(mediador).SubcontrataId.Should().BeNull();
        UltimaConsulta(mediador).EmpresaId.Should().BeNull();
        cut.WaitForAssertion(() => Columna(cut, 0).Should().HaveCount(2));
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Skip(consultasAntes).Distinct().Should().ContainSingle();
    }

    /// <summary>
    /// Búsqueda y empresa se ven como chips con su ✕ y el estado, marcado en la franja; «Limpiar todo» quita los
    /// tres, también de la URL.
    /// </summary>
    [Fact]
    public async Task Los_filtros_se_ven_como_chips_y_en_la_franja_y_Limpiar_todo_los_quita_tambien_de_la_URL()
    {
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Vencido) } };
        var cut = Renderizar(mediador, "trabajadores?q=Salas&estado=Vencido");
        await ElegirEnLaPastilla(cut, "Empresa", "Montajes Ebro S.L.");

        cut.WaitForAssertion(() => TextosDeLosChips(cut).Should().Equal("Búsqueda: \"Salas\"", "Montajes Ebro S.L."));
        cut.MarcadosEnFranja().Should().Equal("Vencidos");
        UltimaConsulta(mediador).EstadoDocumental.Should().Be(nameof(EstadoDocumento.Vencido), "control: el estado de la URL filtra");

        await cut.Find(".barra-filtros-pastillas .limpiar-filtros-barra").ClickAsync(new MouseEventArgs());

        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        uri.Should().NotContain("q=").And.NotContain("estado=").And.NotContain("empresa=");
        UltimaConsulta(mediador).Busqueda.Should().BeNull();
        UltimaConsulta(mediador).EstadoDocumental.Should().BeNull();
        UltimaConsulta(mediador).EmpresaId.Should().BeNull();
        cut.WaitForAssertion(() => cut.FindAll(".barra-filtros-pastillas .chip-filtro").Should().BeEmpty());
        cut.MarcadosEnFranja().Should().Equal("Todos");
    }

    /// <summary>
    /// El estado no tiene chip pero sigue contando como filtro activo: con solo el estado marcado, «Limpiar todo»
    /// aparece y lo quita.
    /// </summary>
    [Fact]
    public async Task Con_solo_el_estado_marcado_Limpiar_todo_aparece_y_lo_quita()
    {
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Vencido) } };
        var cut = Renderizar(mediador, "trabajadores?estado=Vencido");
        TextosDeLosChips(cut).Should().BeEmpty("control: el estado no pinta chip");

        await cut.Find(".barra-filtros-pastillas .limpiar-filtros-barra").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("estado=");
        UltimaConsulta(mediador).EstadoDocumental.Should().BeNull();
        cut.WaitForAssertion(() => cut.MarcadosEnFranja().Should().Equal("Todos"));
    }

    // ------------------------------------- El empleador viaja en la URL (T20)

    [Fact]
    public async Task Elegir_la_Empresa_la_escribe_en_la_url_y_elegir_una_subcontrata_la_sustituye()
    {
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno", EmpresaEbro) } };
        var cut = Renderizar(mediador);
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await ElegirEnLaPastilla(cut, "Empresa", "Montajes Ebro S.L.");

        navegacion.Uri.Should().Contain($"empresa={EmpresaEbro}").And.NotContain("subcontrata=");
        UltimaConsulta(mediador).EmpresaId.Should().Be(EmpresaEbro);

        await ElegirEnLaPastilla(cut, "Empresa", "Aislamientos Nervión S.L. (subcontrata)");

        navegacion.Uri.Should().Contain($"subcontrata={SubcontrataNervion}").And.NotContain("empresa=",
            "son excluyentes y viajan en una sola navegación");
        UltimaConsulta(mediador).SubcontrataId.Should().Be(SubcontrataNervion);
        UltimaConsulta(mediador).EmpresaId.Should().BeNull();
    }

    /// <summary>Recargar o compartir el enlace reproduce la vista: el filtro sale de la URL, no de la memoria.</summary>
    [Fact]
    public void Un_enlace_con_la_Empresa_filtra_la_consulta_y_pinta_su_chip()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Javier", "Salas Moreno", EmpresaEbro), Trabajador("Nuria", "Vega Ortiz", EmpresaDexter) }
        };
        var cut = Renderizar(mediador, $"trabajadores?empresa={EmpresaDexter}");

        UltimaConsulta(mediador).EmpresaId.Should().Be(EmpresaDexter);
        cut.WaitForAssertion(() => TextosDeLosChips(cut).Should().Equal("Dexter Industrial S.A."));
    }

    [Fact]
    public void Un_valor_de_Empresa_en_la_url_que_no_es_un_Id_no_filtra()
    {
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno", EmpresaEbro) } };
        var cut = Renderizar(mediador, "trabajadores?empresa=no-es-un-id");

        UltimaConsulta(mediador).EmpresaId.Should().BeNull();
        TextosDeLosChips(cut).Should().BeEmpty();
    }

    [Fact]
    public async Task Aplicar_un_filtro_guardado_con_Empresa_la_escribe_en_la_url()
    {
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Solo Dexter", "{\"EmpresaId\":\"" + EmpresaDexter + "\"}", DateTime.UtcNow);
        var mediador = new MediatorFalso { Almacen = { Trabajador("Nuria", "Vega Ortiz", EmpresaDexter) }, FiltrosGuardados = { filtro } };
        var cut = Renderizar(mediador, $"trabajadores?subcontrata={SubcontrataNervion}");

        await PulsarEnMasFiltros(cut, "Solo Dexter");

        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        uri.Should().Contain($"empresa={EmpresaDexter}").And.NotContain("subcontrata=",
            "si la Empresa quedara solo en memoria, la siguiente pasada de parámetros repondría la subcontrata de la URL");
        UltimaConsulta(mediador).EmpresaId.Should().Be(EmpresaDexter);
    }

    // ------------------------------------- Borrar un filtro guardado (T9)

    [Fact]
    public async Task Borrar_un_filtro_guardado_pide_confirmacion_y_solo_entonces_lo_borra()
    {
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Solo Vega", "{\"Busqueda\":\"Vega\"}", DateTime.UtcNow);
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno") }, FiltrosGuardados = { filtro } };
        var cut = Renderizar(mediador);

        await PulsarBorrarFiltroGuardado(cut, "Solo Vega");

        mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().BeEmpty("pulsar el aspa solo pide confirmación");
        var dialogo = cut.Find("[role=dialog]");
        dialogo.QuerySelector("h2")!.TextContent.Should().Be("¿Borrar el filtro guardado «Solo Vega»?");
        dialogo.TextContent.Should().Contain("No borra ningún trabajador");

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Borrar filtro").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().Equal([new EliminarFiltroGuardadoCommand(filtro.Id)]);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().ContainSingle(
            "borrado el filtro no se relee la lista: si esa relectura fallara, la confirmación quedaría abierta sobre un filtro que ya no existe");
        await Pastilla(cut, "Más filtros").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".barra-filtros-pastillas .menu-filtro-guardado").Should().BeEmpty());
    }

    [Fact]
    public async Task Cancelar_el_borrado_de_un_filtro_guardado_no_lo_borra()
    {
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Solo Vega", "{\"Busqueda\":\"Vega\"}", DateTime.UtcNow);
        var mediador = new MediatorFalso { FiltrosGuardados = { filtro } };
        var cut = Renderizar(mediador);

        await PulsarBorrarFiltroGuardado(cut, "Solo Vega");
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().BeEmpty();
        await Pastilla(cut, "Más filtros").ClickAsync(new MouseEventArgs());
        cut.FindAll(".barra-filtros-pastillas .menu-filtro-guardado").Should().ContainSingle(c => c.TextContent.Contains("Solo Vega"));
    }

    private static async Task PulsarBorrarFiltroGuardado(IRenderedComponent<Trabajadores> cut, string nombre)
    {
        await Pastilla(cut, "Más filtros").ClickAsync(new MouseEventArgs());
        await cut.Find($".barra-filtros-pastillas [aria-label='Borrar filtro guardado {nombre}']").ClickAsync(new MouseEventArgs());
    }

    /// <summary>
    /// Los filtros guardados viven dentro de «Más filtros», uno por línea con su ✕, y «Guardar
    /// filtro» al final: se ve pero está deshabilitado mientras no haya ningún filtro aplicado
    /// (no hay nada que guardar).
    /// </summary>
    [Fact]
    public async Task Mas_filtros_lleva_los_filtros_guardados_y_Guardar_filtro_deshabilitado_sin_filtros()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Javier", "Salas Moreno") },
            FiltrosGuardados = { new FiltroGuardadoDto(Guid.NewGuid(), "Solo Vega", "{\"Busqueda\":\"Vega\"}", DateTime.UtcNow) }
        };
        var cut = Renderizar(mediador);

        await Pastilla(cut, "Más filtros").ClickAsync(new MouseEventArgs());

        var items = cut.FindAll(".barra-filtros-pastillas [role=menuitem]");
        items.Select(i => i.TextContent.Trim()).Should().Equal("Solo Vega", "", "Guardar filtro");
        items[1].GetAttribute("aria-label").Should().Be("Borrar filtro guardado Solo Vega");
        items.Single(i => i.TextContent.Trim() == "Guardar filtro").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Consulta_ve_la_lista_sin_seleccion_multiple_porque_todo_lo_del_lote_escribe()
    {
        // La selección solo alimenta la barra de lote —Eliminar seleccionados y Asignar a
        // centro…, ICommand que AutorizacionEscrituraBehavior deniega a Consulta—: sin barra,
        // las casillas no servirían para nada, así que tampoco se ofrece el modo.
        this.ConRolDeEscritura(Roles.Consulta);
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso") } });

        cut.Markup.Should().Contain("Alonso", "la lista es lectura: la fila se ve");
        cut.Find("header.cabecera-pagina .acciones-cabecera").QuerySelectorAll("button")
            .Select(b => b.GetAttribute("aria-label") ?? b.TextContent.Trim())
            .Should().Equal(["Atajos de teclado", "Más acciones"], "sin ☑ ni alta: solo el «⋯» con la exportación, que es lectura");
        Pastilla(cut, "Más filtros").Should().NotBeNull("la barra sigue ahí: solo falta el modo de selección");
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty();
        cut.FindAll("tbody input[type=checkbox]").Should().BeEmpty();
    }

    /// <summary>Con Consulta, «x» tampoco enciende una selección que no lleva a ninguna acción.</summary>
    [Fact]
    public async Task Consulta_no_enciende_la_seleccion_con_la_tecla_x()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso") } });
        var atajos = cut.FindComponent<AtajosListaTeclado>();

        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("x"));

        ClasesDeLasFilas(cut).Should().Equal(["fila-enfocada"], "control positivo: la j sí enfocó la fila");
        cut.FindAll("tbody input[type=checkbox]").Should().BeEmpty();
    }

    [Fact]
    public async Task Seleccion_multiple_de_la_cabecera_pinta_las_casillas_y_al_apagarse_suelta_la_seleccion()
    {
        var bea = Trabajador("Bea", "Alonso");
        var cut = Renderizar(new MediatorFalso { Almacen = { bea, Trabajador("Ana", "Moreno") } });
        ConmutadorSeleccion(cut).GetAttribute("aria-label").Should().Be("Selección múltiple");
        ConmutadorSeleccion(cut).GetAttribute("aria-pressed").Should().Be("false");
        cut.FindAll("tbody input[type=checkbox]").Should().BeEmpty("sin el modo no hay casillas de fila");

        await AlternarSeleccionMultiple(cut);

        ConmutadorSeleccion(cut).GetAttribute("aria-pressed").Should().Be("true");
        await cut.Find("tbody input[aria-label='Seleccionar a Bea Alonso']").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Trim().Should().Be("1 seleccionado en esta página");
        cut.FindAll(".barra-acciones-lote button").Select(b => b.TextContent.Trim()).Should().Contain("Asignar a centro…");

        await AlternarSeleccionMultiple(cut);

        cut.FindAll("tbody input[type=checkbox]").Should().BeEmpty();
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty("apagar el modo suelta la selección que ya no se ve");
    }

    /// <summary>
    /// «x» sobre la fila enfocada enciende la selección múltiple (como la maqueta): la casilla
    /// marcada queda a la vista y la barra de lote nunca apunta a una fila sin casilla.
    /// </summary>
    [Fact]
    public async Task La_tecla_x_enciende_la_seleccion_multiple_y_marca_la_fila_enfocada()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso"), Trabajador("Ana", "Moreno") } });
        var atajos = cut.FindComponent<AtajosListaTeclado>();
        cut.FindAll("tbody input[type=checkbox]").Should().BeEmpty("punto de partida: sin el modo");

        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("x"));

        ConmutadorSeleccion(cut).GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("tbody input[aria-label='Seleccionar a Bea Alonso']").HasAttribute("checked").Should().BeTrue();
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Trim().Should().Be("1 seleccionado en esta página");
    }

    // --- Filas (rediseño de listados, fase 1) ------------------------------------------------

    /// <summary>
    /// Una sola celda «Trabajador» con «Apellidos, Nombre» (abre la vista previa) y el DNI debajo;
    /// la columna Empresa; y Documentación solo con la pastilla de estado. Sin la documentación
    /// base del listado («Al día en lo básico» y sus cuatro puntos), que ni siquiera se pide.
    /// </summary>
    [Fact]
    public void La_fila_lleva_la_celda_Trabajador_con_el_DNI_debajo_la_empresa_y_solo_el_estado()
    {
        var javier = Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Urgente);
        var mediador = new MediatorFalso { Almacen = { javier } };
        var cut = Renderizar(mediador);

        cut.FindAll("thead th").Select(th => th.TextContent.Trim()).Should().Equal("Trabajador", "Empresa", "Documentación", "Acciones");
        var fila = FilasDeDatos(cut).Single();
        var celdas = fila.QuerySelectorAll("td");
        celdas[0].QuerySelector(".enlace-nombre-fila")!.TextContent.Trim().Should().Be("Salas Moreno, Javier");
        celdas[0].QuerySelector(".celda-trabajador-dni")!.TextContent.Trim().Should().Be($"DNI {javier.Dto.Dni}");
        celdas[1].TextContent.Trim().Should().Be("Montajes Ebro S.L.");
        celdas[2].QuerySelector(".badge")!.TextContent.Trim().Should().Be("Por vencer", "Urgente y Próximo se rotulan igual");
        celdas[2].QuerySelector(".badge")!.ClassList.Should().Contain("badge-advertencia", "y con el mismo tono: lo urgente ya no va en rojo en la pastilla");
        celdas[2].TextContent.Trim().Should().Be("Por vencer", "solo la pastilla: el documento causante y «vigentes/total» son fase 2");
        cut.Markup.Should().NotContain("Al día en lo básico").And.NotContain("Documentación base");
        cut.FindAll(".panel-doc-base").Should().BeEmpty();
        mediador.Enviadas.Should().NotContain(e => e.GetType().Name == "ObtenerDocumentacionBaseTrabajadoresQuery",
            "la documentación base deja de resumirse en el listado: vive en la vista previa");
    }

    [Fact]
    public void Sin_documento_de_identidad_la_celda_no_pinta_una_linea_DNI_vacia()
    {
        var sinDni = Trabajador("Javier", "Salas Moreno");
        var mediador = new MediatorFalso { Almacen = { sinDni with { Dto = sinDni.Dto with { Dni = null } } } };
        var cut = Renderizar(mediador);

        Columna(cut, 0).Should().Equal(["Salas Moreno"], "control positivo: la fila se pinta");
        cut.FindAll(".celda-trabajador-dni").Should().BeEmpty();
    }

    /// <summary>
    /// Por defecto, el peor estado primero (la consulta ordena por EstadoDocumental ascendente), y
    /// la fila se tinta: Vencido en peligro, Urgente en aviso, el resto sin tinte.
    /// </summary>
    [Fact]
    public void Por_defecto_el_peor_estado_va_primero_y_las_filas_se_tintan_por_estado()
    {
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                Trabajador("Ana", "Alonso"),
                Trabajador("Bea", "Benito", estado: EstadoDocumento.Urgente),
                Trabajador("Carla", "Campos", estado: EstadoDocumento.Vencido),
                Trabajador("Dani", "Díaz", estado: EstadoDocumento.Proximo),
            }
        };
        var cut = Renderizar(mediador);

        var consulta = UltimaConsulta(mediador);
        consulta.OrdenarPor.Should().Be(nameof(TrabajadorListaDto.EstadoDocumental));
        consulta.Descendente.Should().BeFalse();
        Columna(cut, 0).Should().Equal("Campos", "Benito", "Díaz", "Alonso");
        ClasesDeLasFilas(cut).Should().Equal("fila-tintada-peligro", "fila-tintada-aviso", string.Empty, string.Empty);
    }

    /// <summary>El foco de j/k se suma al tinte, no lo sustituye.</summary>
    [Fact]
    public async Task La_fila_enfocada_con_j_conserva_el_tinte_de_su_estado()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Almacen = { Trabajador("Ana", "Alonso"), Trabajador("Carla", "Campos", estado: EstadoDocumento.Vencido) }
        });
        var atajos = cut.FindComponent<AtajosListaTeclado>();

        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        ClasesDeLasFilas(cut).Should().Equal("fila-enfocada fila-tintada-peligro", string.Empty);

        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        ClasesDeLasFilas(cut).Should().Equal("fila-tintada-peligro", "fila-enfocada");
    }

    /// <summary>Con 20 o menos al tamaño mínimo no hay nada que paginar: sin paginador.</summary>
    [Fact]
    public void Con_veinte_o_menos_no_se_pinta_el_paginador()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 20; i++)
            mediador.Almacen.Add(Trabajador("Nombre", $"Apellido {i:00}"));
        var cut = Renderizar(mediador);

        Columna(cut, 0).Should().HaveCount(20, "control positivo: la página está llena");
        cut.FindAll(".paginador-simple").Should().BeEmpty();
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("20");
    }

    // --- Estados ------------------------------------------------------------------------------

    [Fact]
    public async Task Mientras_carga_se_ve_el_esqueleto_y_no_el_vacio()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno") } };
        mediador.Retener = p => p is ObtenerTrabajadoresQuery ? respuesta.Task : null;
        Registrar(mediador, "trabajadores");

        var cut = Render<Trabajadores>();

        cut.WaitForAssertion(() => cut.FindAll(".esqueleto[aria-busy=true]").Should().ContainSingle());
        cut.Markup.Should().NotContain("Aún no hay trabajadores", "todavía no se sabe si hay alguno");

        mediador.Retener = null;
        await cut.InvokeAsync(() => respuesta.SetResult(mediador.Filtrar(UltimaConsulta(mediador))));

        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Salas Moreno"));
        cut.FindAll(".esqueleto").Should().BeEmpty();
    }

    [Fact]
    public void Sin_trabajadores_el_vacio_invita_a_dar_de_alta_el_primero()
    {
        var cut = Renderizar(new MediatorFalso());

        var vacio = cut.Find(".estado-vacio");
        vacio.QuerySelector("h3")!.TextContent.Trim().Should().Be("Aún no hay trabajadores");
        vacio.TextContent.Should().Contain("Da de alta el primero para empezar a controlar su documentación.");
        vacio.QuerySelector("button")!.TextContent.Trim().Should().Be("+ Nuevo trabajador");
    }

    /// <summary>
    /// La consulta del arranque (sin filtro) tarda; mientras tanto se filtra
    /// por «Vencido», que responde en seguida sin nada. Cuando la vieja llega,
    /// no puede pisar el total: la pantalla sigue diciendo que ninguno coincide.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_lenta_de_la_pregunta_anterior_no_pisa_el_resultado_de_la_vigente()
    {
        var respuestaVieja = new TaskCompletionSource<object>();
        var retenida = false;
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz"), Trabajador("Luis", "Iglesias Rey") }
        };
        mediador.Retener = p =>
        {
            if (retenida || p is not ObtenerTrabajadoresQuery { EstadoDocumental: null }) return null;
            retenida = true;
            return respuestaVieja.Task;
        };
        Registrar(mediador, "trabajadores");
        var cut = Render<Trabajadores>();
        cut.WaitForAssertion(() => retenida.Should().BeTrue());

        await AlternarEnLaFranja(cut, "Vencidos");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ningún trabajador con estos filtros"));

        await cut.InvokeAsync(() => respuestaVieja.SetResult(
            mediador.Filtrar(new ObtenerTrabajadoresQuery(null))));

        // Cualquier repintado posterior enseña el estado que dejó la respuesta
        // vieja; se fuerza uno para no depender de cuál llegue.
        cut.Render();

        cut.Markup.Should().Contain("Ningún trabajador con estos filtros",
            "la respuesta vieja era de la lista sin filtro, no de la pregunta vigente");
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("0");
    }

    // --- Orden y página -----------------------------------------------------------------------

    /// <summary>
    /// El doble ordena según lo que recibe. El orden por defecto (el peor estado primero) deja a
    /// Zubiri, que tiene algo vencido, delante: ni el ascendente ni el descendente por apellidos.
    /// </summary>
    [Fact]
    public async Task Pulsar_la_cabecera_Trabajador_ordena_por_apellidos_y_la_segunda_vez_al_reves()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Bea", "Alonso"), Trabajador("Ana", "Moreno"), Trabajador("Carlos", "Zubiri", estado: EstadoDocumento.Vencido) }
        };
        var cut = Renderizar(mediador);
        Columna(cut, 0).Should().Equal(["Zubiri", "Alonso", "Moreno"], "punto de partida: el peor estado primero");

        await CabeceraOrdenable(cut, "Trabajador").ClickAsync(new MouseEventArgs());

        UltimaConsulta(mediador).OrdenarPor.Should().Be(nameof(TrabajadorListaDto.Apellidos));
        UltimaConsulta(mediador).Descendente.Should().BeFalse();
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal(["Alonso", "Moreno", "Zubiri"]));

        await CabeceraOrdenable(cut, "Trabajador").ClickAsync(new MouseEventArgs());

        UltimaConsulta(mediador).OrdenarPor.Should().Be(nameof(TrabajadorListaDto.Apellidos));
        UltimaConsulta(mediador).Descendente.Should().BeTrue("la segunda pulsación invierte el orden");
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal(["Zubiri", "Moreno", "Alonso"]));
    }

    [Fact]
    public async Task Pulsar_la_cabecera_Empresa_ordena_por_el_empleador()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Bea", "Alonso"), Trabajador("Ana", "Moreno", empresaId: EmpresaDexter) }
        };
        var cut = Renderizar(mediador);

        await CabeceraOrdenable(cut, "Empresa").ClickAsync(new MouseEventArgs());

        UltimaConsulta(mediador).OrdenarPor.Should().Be(nameof(TrabajadorListaDto.EmpleadorNombre));
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal(["Moreno", "Alonso"], "Dexter va antes que Montajes"));
    }

    private static IElement CabeceraOrdenable(IRenderedComponent<Trabajadores> cut, string titulo) =>
        cut.FindAll("thead th").Single(th => th.TextContent.Trim() == titulo).QuerySelector("button")!;

    /// <summary>
    /// 25 trabajadores: la página 1 son los 20 primeros por apellidos y la 2,
    /// los cinco últimos. Si la pantalla mandara siempre la página 1, la
    /// segunda repetiría la primera.
    /// </summary>
    [Fact]
    public async Task Pasar_a_la_pagina_siguiente_pide_la_pagina_2_y_cambiar_el_tamano_vuelve_a_la_1()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Trabajador("Nombre", $"Apellido {i:00}"));
        var cut = Renderizar(mediador);
        Columna(cut, 0).Should().HaveCount(20).And.StartWith("Apellido 01");
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 1 de 2");

        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());

        UltimaConsulta(mediador).Pagina.Should().Be(2);
        UltimaConsulta(mediador).TamanoPagina.Should().Be(20);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal(
            ["Apellido 21", "Apellido 22", "Apellido 23", "Apellido 24", "Apellido 25"]));
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2").And.Contain("25 trabajador(es)");

        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        UltimaConsulta(mediador).Pagina.Should().Be(1);
        UltimaConsulta(mediador).TamanoPagina.Should().Be(50);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().HaveCount(25));
    }

    /// <summary>
    /// Cambiar el tamaño de página pide la página 1 del tamaño nuevo UNA vez.
    /// Mismo motivo que <see cref="Buscar_desde_la_caja_navega_una_vez_y_hace_una_sola_consulta"/>:
    /// <c>SetCurrentPageIndexAsync</c> ya avisa a QuickGrid aunque la página no
    /// cambie, y refrescar además la rejilla pedía lo mismo dos veces. Los 25
    /// trabajadores son los mismos antes y después, así que con el total quieto
    /// lo que se cuenta es lo que pide la página.
    /// </summary>
    [Fact]
    public async Task Cambiar_el_tamano_de_pagina_hace_una_sola_consulta()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Trabajador("Nombre", $"Apellido {i:00}"));
        var cut = Renderizar(mediador);
        var consultasAntes = ConsultasDeLista(mediador);

        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        cut.WaitForAssertion(() => Columna(cut, 0).Should().HaveCount(25));
        UltimaConsulta(mediador).TamanoPagina.Should().Be(50);
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1,
            "avisar a la paginación y refrescar la rejilla son dos formas de pedir lo mismo");
    }

    // --- Filtros y URL ------------------------------------------------------------------------

    [Fact]
    public async Task Filtrar_por_empresa_manda_su_id_y_filtrar_por_subcontrata_suelta_el_de_empresa()
    {
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                Trabajador("Javier", "Salas Moreno", empresaId: EmpresaEbro),
                Trabajador("Pedro", "Roldán Cano", empresaId: EmpresaDexter),
                Trabajador("Marta", "Duarte Gil", subcontrataId: SubcontrataNervion),
            }
        };
        var cut = Renderizar(mediador);

        await ElegirEnLaPastilla(cut, "Empresa", "Dexter Industrial S.A.");

        UltimaConsulta(mediador).EmpresaId.Should().Be(EmpresaDexter);
        UltimaConsulta(mediador).SubcontrataId.Should().BeNull();
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Roldán Cano"));

        await ElegirEnLaPastilla(cut, "Empresa", "Aislamientos Nervión S.L. (subcontrata)");

        UltimaConsulta(mediador).SubcontrataId.Should().Be(SubcontrataNervion);
        UltimaConsulta(mediador).EmpresaId.Should().BeNull("los dos filtros de empleador se excluyen");
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Duarte Gil"));
    }

    [Fact]
    public async Task Quitar_los_filtros_desde_el_vacio_los_quita_de_la_url_y_de_la_consulta()
    {
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz") } };
        var cut = Renderizar(mediador, "trabajadores?q=Nadie&estado=Vencido");
        var navegacion = Services.GetRequiredService<NavigationManager>();
        cut.Markup.Should().Contain("Ningún trabajador con estos filtros", "es el punto de partida de este caso");
        UltimaConsulta(mediador).Busqueda.Should().Be("Nadie");
        UltimaConsulta(mediador).EstadoDocumental.Should().Be("Vencido");
        var navegaciones = ContarNavegaciones();
        var consultasAntes = ConsultasDeLista(mediador);

        await cut.FindAll(".estado-vacio button").Single(b => b.TextContent.Trim() == "Quitar los filtros").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().NotContain("q=").And.NotContain("estado=",
            "si solo se quitaran en memoria, la siguiente pasada de parámetros los repondría desde la URL");
        UltimaConsulta(mediador).Busqueda.Should().BeNull();
        UltimaConsulta(mediador).EstadoDocumental.Should().BeNull();
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Salas Moreno", "Vega Ortiz"));
        cut.FindAll(".chip-filtro").Should().BeEmpty();
        navegaciones().Should().Be(1, "la búsqueda y el estado se quitan de la URL en una sola navegación");
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Skip(consultasAntes).Distinct().Should().ContainSingle(
            "es una sola pregunta nueva; aquí el total pasa de 0 a 2 y QuickGrid la repite idéntica por su cuenta, así que cuántas "
            + "veces se hace lo cuenta Quitar_los_filtros_sin_cambiar_el_total_navega_una_vez_y_hace_una_sola_consulta");
    }

    /// <summary>
    /// Recuento exacto de «Quitar los filtros»: una navegación y una consulta.
    /// El almacén vacío deja el total en 0 antes y después; si cambiara,
    /// QuickGrid volvería a pedir la misma página en el render siguiente (ver
    /// <see cref="Buscar_desde_la_caja_navega_una_vez_y_hace_una_sola_consulta"/>)
    /// y el recuento mezclaría esa repetición con lo que pide la página.
    /// </summary>
    [Fact]
    public async Task Quitar_los_filtros_sin_cambiar_el_total_navega_una_vez_y_hace_una_sola_consulta()
    {
        var mediador = new MediatorFalso();
        var cut = Renderizar(mediador, "trabajadores?q=Nadie&estado=Vencido");
        cut.Markup.Should().Contain("Ningún trabajador con estos filtros", "es el punto de partida de este caso");
        var navegaciones = ContarNavegaciones();
        var consultasAntes = ConsultasDeLista(mediador);

        await cut.FindAll(".estado-vacio button").Single(b => b.TextContent.Trim() == "Quitar los filtros").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Aún no hay trabajadores"));
        navegaciones().Should().Be(1, "la búsqueda y el estado se quitan de la URL en una sola navegación");
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1,
            "quitar los cuatro filtros es una sola consulta, y la pasada de parámetros que sigue a la navegación ya encuentra la URL igual a la pantalla");
        UltimaConsulta(mediador).Busqueda.Should().BeNull();
        UltimaConsulta(mediador).EstadoDocumental.Should().BeNull();
    }

    /// <summary>
    /// Volver atrás, o pegar un enlace, a otro <c>?q=</c> estando ya en la
    /// página: el chip y la lista tienen que hablar de la misma búsqueda.
    /// </summary>
    [Fact]
    public async Task Navegar_a_otra_busqueda_dentro_de_la_pagina_vuelve_a_pedir_la_lista()
    {
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz") } };
        var cut = Renderizar(mediador, "trabajadores?q=Salas");
        Columna(cut, 0).Should().Equal("Salas Moreno");

        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("trabajadores?q=Vega"));

        cut.WaitForAssertion(() => UltimaConsulta(mediador).Busqueda.Should().Be("Vega"));
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Vega Ortiz"));
        cut.Find(".chip-filtro").TextContent.Should().Contain("Vega");
    }

    /// <summary>
    /// La búsqueda de un filtro guardado se escribe en <c>?q=</c>. Si solo se
    /// aplicara en memoria, cambiar después el filtro de documentación (que sí
    /// escribe la URL) la borraría al releer un <c>?q=</c> vacío.
    /// </summary>
    [Fact]
    public async Task Un_filtro_guardado_con_busqueda_sobrevive_a_cambiar_la_documentacion()
    {
        var filtroId = Guid.NewGuid();
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz") },
            FiltrosGuardados = { new FiltroGuardadoDto(filtroId, "Solo Vega", "{\"Busqueda\":\"Vega\"}", DateTime.UtcNow) }
        };
        var cut = Renderizar(mediador);

        await PulsarEnMasFiltros(cut, "Solo Vega");

        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Vega");
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Vega Ortiz"));

        await AlternarEnLaFranja(cut, "Sin incidencias");

        UltimaConsulta(mediador).Busqueda.Should().Be("Vega", "cambiar un filtro no puede soltar otro que sigue puesto");
        UltimaConsulta(mediador).EstadoDocumental.Should().Be("Vigente,SinCaducidad", "«Sin incidencias» marca los dos estados que se rotulan así");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Vega");
    }

    /// <summary>
    /// Buscar desde la propia página escribe el campo, después la URL, y
    /// recarga una vez. La navegación vuelve a pasar por OnParametersSetAsync,
    /// que encuentra la URL igual a la pantalla y no repite la consulta; si la
    /// repitiera, cada búsqueda costaría dos.
    ///
    /// <para>
    /// El total se queda en 1 (de «Salas» a «Vega») a propósito: cuando el
    /// total cambia, QuickGrid vuelve a pedir la misma página por su cuenta en
    /// el render siguiente (su pila: <c>QuickGrid.OnParametersSetAsync</c> →
    /// <c>RefreshDataCoreAsync</c>), y el recuento mezclaría esa repetición
    /// con lo que pide la página.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Buscar_desde_la_caja_navega_una_vez_y_hace_una_sola_consulta()
    {
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz") } };
        var cut = Renderizar(mediador, "trabajadores?q=Salas");
        Columna(cut, 0).Should().Equal("Salas Moreno");
        var navegaciones = ContarNavegaciones();
        var consultasAntes = ConsultasDeLista(mediador);

        await cut.InvokeAsync(() => CajaDeBusqueda(cut).Instance.ValorChanged.InvokeAsync("Vega"));

        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Vega Ortiz"));
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Vega");
        navegaciones().Should().Be(1);
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1, "la pasada de parámetros que sigue a la navegación no vuelve a pedir la lista");
        UltimaConsulta(mediador).Busqueda.Should().Be("Vega");
    }

    /// <summary>
    /// Lo mismo con el filtro de documentación, que también vive en la URL
    /// (<c>?estado=</c>). También aquí el total se queda en 1, por el mismo
    /// motivo que en la búsqueda: con la franja un clic solo añade o quita
    /// estados, así que el cambio que deja el total quieto es sumar «Por vencer»
    /// a «Vencidos» cuando nadie está por vencer. Que el filtro cambió se
    /// comprueba en la consulta y en la URL; las filas son las mismas.
    /// </summary>
    [Fact]
    public async Task Cambiar_la_documentacion_navega_una_vez_y_hace_una_sola_consulta()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz", estado: EstadoDocumento.Vencido) }
        };
        var cut = Renderizar(mediador, "trabajadores?estado=Vencido");
        Columna(cut, 0).Should().Equal("Vega Ortiz");
        var navegaciones = ContarNavegaciones();
        var consultasAntes = ConsultasDeLista(mediador);

        await AlternarEnLaFranja(cut, "Por vencer");

        cut.WaitForAssertion(() => cut.MarcadosEnFranja().Should().Equal("Vencidos", "Por vencer"));
        Columna(cut, 0).Should().Equal("Vega Ortiz");
        Uri.UnescapeDataString(Services.GetRequiredService<NavigationManager>().Uri).Should().EndWith("estado=Vencido,Urgente,Proximo");
        navegaciones().Should().Be(1);
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1, "la pasada de parámetros que sigue a la navegación no vuelve a pedir la lista");
        UltimaConsulta(mediador).EstadoDocumental.Should().Be("Vencido,Urgente,Proximo");
    }

    /// <summary>
    /// El handler busca también en el alias, y la caja lo promete en su
    /// marcador. «juanjo» no está en el nombre, los apellidos ni el DNI de
    /// nadie: solo en el alias de uno.
    /// </summary>
    [Fact]
    public async Task Buscar_por_alias_encuentra_al_trabajador_de_ese_alias()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("José Juan", "Pérez Gil", alias: "Juanjo"), Trabajador("Ana", "Vega Ortiz") }
        };
        var cut = Renderizar(mediador);

        await cut.InvokeAsync(() => CajaDeBusqueda(cut).Instance.ValorChanged.InvokeAsync("juanjo"));

        UltimaConsulta(mediador).Busqueda.Should().Be("juanjo");
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Pérez Gil"));
    }

    // --- Acciones de fila ---------------------------------------------------------------------

    /// <summary>
    /// El menú se busca DENTRO de las filas: la cabecera («⋯») y la barra de filtros (pastillas,
    /// «Más filtros») también llevan disparadores de menú.
    /// </summary>
    private static async Task PulsarEnElMenuDeLaFila(IRenderedComponent<Trabajadores> cut, int fila, string item)
    {
        await cut.FindAll("tbody .menu-acciones")[fila].QuerySelector(".menu-acciones-disparador")!.ClickAsync(new MouseEventArgs());
        await cut.FindAll("tbody .menu-acciones")[fila].QuerySelectorAll(".menu-acciones-item")
            .Single(i => i.TextContent.Trim() == item).ClickAsync(new MouseEventArgs());
    }

    [Fact]
    public async Task Eliminar_pide_confirmacion_con_su_efecto_borra_esa_fila_y_deshacer_la_restaura()
    {
        var ana = Trabajador("Ana", "Moreno");
        var mediador = new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso"), ana } };
        var cut = Renderizar(mediador);

        await PulsarEnElMenuDeLaFila(cut, 1, "Eliminar");

        var dialogo = cut.Find("[role=dialog]");
        dialogo.TextContent.Should().Contain("¿Eliminar a Ana Moreno?")
            .And.Contain("Se ocultará de las listas activas. Podrás deshacerlo desde el aviso que aparecerá.");
        mediador.Enviadas.OfType<EliminarTrabajadorCommand>().Should().BeEmpty("abrir el diálogo no borra nada");

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarTrabajadorCommand>().Should().Equal([new EliminarTrabajadorCommand(ana.Dto.Id)]);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Alonso"));

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Single(m => m.TextoAccion == "Deshacer");
        await cut.InvokeAsync(aviso.OnAccion!);

        mediador.Enviadas.OfType<RestaurarTrabajadorCommand>().Should().Equal([new RestaurarTrabajadorCommand(ana.Dto.Id)]);
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Alonso", "Moreno"));
    }


    /// <summary>
    /// El Workspace no es modal: con la ficha del trabajador abierta, la baja se
    /// confirma desde la fila que queda detrás. La ficha ya no tiene baja propia
    /// (P41b), así que la lista es quien la retira (hallazgo de Codex).
    /// </summary>
    [Fact]
    public async Task Eliminar_al_trabajador_cuya_ficha_esta_abierta_retira_la_ficha()
    {
        var ana = Trabajador("Ana", "Moreno");
        var mediador = new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso"), ana } };
        var cut = Renderizar(mediador);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Trabajador, ana.Dto.Id, "Ana Moreno", "operacion"));
        workspace.EstaAbierto.Should().BeTrue("control positivo: la ficha estaba abierta");

        await PulsarEnElMenuDeLaFila(cut, 1, "Eliminar");
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarTrabajadorCommand>().Should().ContainSingle("la baja se ejecutó");
        workspace.EstaAbierto.Should().BeFalse("una ficha abierta de un trabajador ya dado de baja no puede seguir editable");
    }
    [Fact]
    public async Task Abrir_Trabajador_360_desde_el_menu_navega_a_la_ficha_de_esa_fila()
    {
        var ana = Trabajador("Ana", "Moreno");
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso"), ana } });

        await PulsarEnElMenuDeLaFila(cut, 1, "Abrir ficha 360");

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"/trabajadores/{ana.Dto.Id}");
    }

    [Fact]
    public async Task Los_atajos_j_x_y_Enter_recorren_marcan_y_abren_la_vista_previa_de_la_fila()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso"), Trabajador("Ana", "Moreno") } });
        await AlternarSeleccionMultiple(cut);
        var atajos = cut.FindComponent<AtajosListaTeclado>();

        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("x"));

        cut.Find("tbody input[aria-label='Seleccionar a Ana Moreno']").HasAttribute("checked").Should().BeTrue();
        cut.Find("tbody input[aria-label='Seleccionar a Bea Alonso']").HasAttribute("checked").Should().BeFalse();

        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("Enter"));

        cut.WaitForAssertion(() => cut.Find("aside.drawer-preview-trabajador .nombre-cabecera-preview-trabajador")
            .TextContent.Trim().Should().Be("Ana Moreno"));
    }

    // --- Carreras y dobles clics --------------------------------------------------------------

    /// <summary>
    /// La vista previa de A tarda; mientras tanto se abre la de B, que responde
    /// en seguida. Cuando A llega, el panel sigue siendo de B.
    /// </summary>
    [Fact]
    public async Task La_vista_previa_de_una_fila_no_se_pisa_con_la_respuesta_tardia_de_otra()
    {
        var a = Trabajador("Bea", "Alonso");
        var b = Trabajador("Ana", "Moreno");
        var respuestaDeA = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso
        {
            Almacen = { a, b },
            Retener = p => p is ObtenerTrabajadorPorIdQuery q && q.Id == a.Dto.Id ? respuestaDeA.Task : null
        };
        var cut = Renderizar(mediador);

        // Sin await: el drawer espera a la consulta retenida.
        var clicA = cut.FindAll("td .enlace-nombre-fila").First(e => e.TextContent.Trim() == "Alonso, Bea").ClickAsync(new MouseEventArgs());
        await cut.FindAll("td .enlace-nombre-fila").First(e => e.TextContent.Trim() == "Moreno, Ana").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find(".nombre-cabecera-preview-trabajador").TextContent.Trim().Should().Be("Ana Moreno"));

        await cut.InvokeAsync(() => respuestaDeA.SetResult(mediador.Detalle(a.Dto.Id)!));
        await clicA;
        cut.Render();

        cut.Find(".nombre-cabecera-preview-trabajador").TextContent.Trim().Should().Be("Ana Moreno",
            "la respuesta de Bea era de otra pregunta");
    }

    /// <summary>
    /// Dos «Guardar» del alta que llegan antes de que el botón se pinte
    /// deshabilitado: un solo comando. Se invoca el manejador del botón dos
    /// veces, que es lo que el servidor recibe en ese caso (Boton mantiene el
    /// @onclick enganchado aunque esté disabled).
    /// </summary>
    [Fact]
    public async Task Dos_Guardar_seguidos_del_alta_mandan_un_solo_comando()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Perfil = PerfilVocabularioTenant.ClienteDirecto };
        mediador.Empresas.RemoveAt(1);
        mediador.Retener = p => p is CrearTrabajadorCommand ? respuesta.Task : null;
        var cut = Renderizar(mediador);

        await AbrirAltaAsync(cut);
        var guardar = cut.FindComponents<Boton>().Single(b => b.Find("button").TextContent.Trim() == "Guardar");

        var primero = cut.InvokeAsync(() => guardar.Instance.OnClick.InvokeAsync());
        var segundo = cut.InvokeAsync(() => guardar.Instance.OnClick.InvokeAsync());

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle(
            "el segundo clic llega con el primero todavía en vuelo");
        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Single().EmpresaId.Should().Be(EmpresaEbro,
            "perfil Cliente Directo con una única Empresa: se resuelve en silencio");

        await cut.InvokeAsync(() => respuesta.SetResult(Result.Exito(Guid.NewGuid())));
        await Task.WhenAll(primero, segundo);
    }

    /// <summary>
    /// En «Asignar a centro…», elegir el centro A (lento, con faltantes) y
    /// después el B (sin faltantes): el aviso no puede hablar de A.
    /// </summary>
    [Fact]
    public async Task El_aviso_de_faltantes_es_del_centro_elegido_y_no_del_anterior_que_respondio_tarde()
    {
        var bea = Trabajador("Bea", "Alonso");
        var centroA = new CentroSelectorDto(Guid.NewGuid(), "Planta Zaragoza", "Refrielectric S.L.", "Montajes Ebro S.L.");
        var centroB = new CentroSelectorDto(Guid.NewGuid(), "Centro Logístico Norte", "Refrielectric S.L.", "Montajes Ebro S.L.");
        var respuestaDeA = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso
        {
            Almacen = { bea },
            Centros = { centroA, centroB },
            Retener = p => p is ObtenerDocumentosFaltantesParaAsignacionQuery q && q.CentroIds.Single() == centroA.Id ? respuestaDeA.Task : null
        };
        var cut = Renderizar(mediador);

        await AlternarSeleccionMultiple(cut);
        await cut.Find("tbody input[aria-label='Seleccionar a Bea Alonso']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Asignar a centro…").ClickAsync(new MouseEventArgs());
        var campo = cut.FindComponent<CampoBuscarSelect>();

        var eleccionA = cut.InvokeAsync(() => campo.Instance.ValorChanged.InvokeAsync(centroA.Id.ToString()));
        await cut.InvokeAsync(() => campo.Instance.ValorChanged.InvokeAsync(centroB.Id.ToString()));

        IReadOnlyList<DocumentoFaltanteDto> faltantesDeA =
            [new(bea.Dto.Id, "Bea Alonso", centroA.Id, centroA.Nombre, Guid.NewGuid(), "Formación PRL específica")];
        await cut.InvokeAsync(() => respuestaDeA.SetResult(faltantesDeA));
        await eleccionA;
        cut.Render();

        cut.FindAll(".alerta-preflight-asignacion").Should().BeEmpty("el centro elegido es B, que no deja faltantes");
        cut.FindAll(".modal-pie button, [role=dialog] button").Select(b => b.TextContent.Trim())
            .Should().Contain("Asignar").And.NotContain("Asignar igualmente");
    }

    private static IElement BotonAsignarACentro(IRenderedComponent<Trabajadores> cut) =>
        cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Asignar a centro…");

    private static async Task AbrirAsignarACentro(IRenderedComponent<Trabajadores> cut, Fila trabajador)
    {
        await AlternarSeleccionMultiple(cut);
        await cut.Find($"tbody input[aria-label='Seleccionar a {trabajador.Dto.Nombre} {trabajador.Dto.Apellidos}']")
            .ChangeAsync(new ChangeEventArgs { Value = true });
        await BotonAsignarACentro(cut).ClickAsync(new MouseEventArgs());
    }

    private static List<string> BotonesDelPieDelDialogo(IRenderedComponent<Trabajadores> cut) =>
        cut.FindAll("[role=dialog] .modal-pie button").Select(b => b.TextContent.Trim()).ToList();

    /// <summary>
    /// Se elige el centro A (su consulta de faltantes tarda), se cierra el
    /// diálogo con Cancelar y se reabre. Cuando A responde, el diálogo nuevo
    /// no tiene centro elegido: ni aviso ni «Asignar igualmente».
    /// </summary>
    [Fact]
    public async Task Cerrar_y_reabrir_Asignar_a_centro_descarta_los_faltantes_tardios_del_dialogo_anterior()
    {
        var bea = Trabajador("Bea", "Alonso");
        var centroA = new CentroSelectorDto(Guid.NewGuid(), "Planta Zaragoza", "Refrielectric S.L.", "Montajes Ebro S.L.");
        var respuestaDeA = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso
        {
            Almacen = { bea },
            Centros = { centroA },
            Retener = p => p is ObtenerDocumentosFaltantesParaAsignacionQuery ? respuestaDeA.Task : null
        };
        var cut = Renderizar(mediador);
        await AbrirAsignarACentro(cut, bea);
        var campo = cut.FindComponent<CampoBuscarSelect>();

        // Sin await: la consulta de faltantes de A queda retenida.
        var eleccionA = cut.InvokeAsync(() => campo.Instance.ValorChanged.InvokeAsync(centroA.Id.ToString()));
        await cut.FindAll("[role=dialog] .modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        // D-05: con el centro ya elegido, «Cancelar» pregunta como la X; se descarta para cerrar.
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());
        cut.FindComponents<CampoBuscarSelect>().Should().BeEmpty("Cancelar cierra el diálogo");
        await BotonAsignarACentro(cut).ClickAsync(new MouseEventArgs());
        cut.FindComponent<CampoBuscarSelect>().Instance.Valor.Should().BeNullOrEmpty("es un diálogo nuevo");

        IReadOnlyList<DocumentoFaltanteDto> faltantesDeA =
            [new(bea.Dto.Id, "Bea Alonso", centroA.Id, centroA.Nombre, Guid.NewGuid(), "Formación PRL específica")];
        await cut.InvokeAsync(() => respuestaDeA.SetResult(faltantesDeA));
        await eleccionA;
        cut.Render();

        cut.FindComponent<CampoBuscarSelect>().Instance.Valor.Should().BeNullOrEmpty();
        cut.FindAll(".alerta-preflight-asignacion").Should().BeEmpty(
            "la respuesta era del centro A de un diálogo ya cerrado; el nuevo no tiene centro elegido");
        BotonesDelPieDelDialogo(cut).Should().Contain("Asignar").And.NotContain("Asignar igualmente");
    }

    /// <summary>
    /// La comprobación de faltantes es una lectura hecha al elegir el centro, y
    /// CrearAsignacionesCommand ni la repite ni se detiene por documentos: el
    /// aviso cuenta lo que faltaba al comprobarlo y que asignar no lo cambia,
    /// sin prometer qué «quedará» al confirmar.
    /// </summary>
    [Fact]
    public async Task El_aviso_de_faltantes_dice_lo_que_faltaba_al_comprobarlo_y_que_asignar_no_lo_crea_ni_lo_impide()
    {
        var bea = Trabajador("Bea", "Alonso");
        var centro = new CentroSelectorDto(Guid.NewGuid(), "Planta Zaragoza", "Refrielectric S.L.", "Montajes Ebro S.L.");
        var mediador = new MediatorFalso
        {
            Almacen = { bea },
            Centros = { centro },
            Faltantes = _ => [new(bea.Dto.Id, "Bea Alonso", centro.Id, centro.Nombre, Guid.NewGuid(), "Formación PRL específica")]
        };
        var cut = Renderizar(mediador);
        await AbrirAsignarACentro(cut, bea);

        await cut.InvokeAsync(() => cut.FindComponent<CampoBuscarSelect>().Instance.ValorChanged.InvokeAsync(centro.Id.ToString()));

        var aviso = string.Join(' ', cut.Find(".alerta-preflight-asignacion").TextContent
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        aviso.Should().Contain("Al comprobarlo faltaban 1 documento(s) que se piden:")
            .And.Contain("Bea Alonso — Formación PRL específica")
            .And.Contain("Asignar no crea esos documentos, y que falten no impide la asignación.")
            .And.NotContain("quedarán sin", "la consulta previa no sabe qué quedará al confirmar")
            .And.NotContainEquivalentOf("obligatori", "es configuración (se pide), no una obligación legal");
        BotonesDelPieDelDialogo(cut).Should().Contain("Asignar igualmente");
    }

    /// <summary>Lo mismo con la baja en lote: solo se retira la ficha si su trabajador iba en el lote.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Eliminar_en_lote_retira_la_ficha_abierta_solo_si_su_trabajador_iba_en_el_lote(bool ibaEnElLote)
    {
        var ana = Trabajador("Ana", "Moreno");
        var bea = Trabajador("Bea", "Alonso");
        var mediador = new MediatorFalso { Almacen = { bea, ana } };
        var cut = Renderizar(mediador);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        var abierta = ibaEnElLote ? ana : bea;
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Trabajador, abierta.Dto.Id, "Ficha abierta", "operacion"));
        workspace.EstaAbierto.Should().BeTrue("control positivo: la ficha estaba abierta");

        await AlternarSeleccionMultiple(cut);
        await cut.Find("tbody input[aria-label='Seleccionar a Ana Moreno']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados")
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarTrabajadoresCommand>().Single().Ids.Should().Equal([ana.Dto.Id],
            "el caso solo vale si el lote pidió a ese trabajador y a nadie más");
        workspace.EstaAbierto.Should().Be(!ibaEnElLote);
    }

    /// <summary>La guarda de la retirada: un lote que no eliminó NADA no toca la ficha abierta de un trabajador que iba en él.</summary>
    [Fact]
    public async Task Un_lote_que_no_elimina_nada_no_retira_la_ficha_abierta()
    {
        var ana = Trabajador("Ana", "Moreno");
        var mediador = new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso"), ana }, EliminadosForzados = 0 };
        var cut = Renderizar(mediador);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Trabajador, ana.Dto.Id, "Ana Moreno", "operacion"));

        await AlternarSeleccionMultiple(cut);
        await cut.Find("tbody input[aria-label='Seleccionar a Ana Moreno']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarTrabajadoresCommand>().Single().Ids.Should().Equal([ana.Dto.Id], "el caso solo vale si el lote pidió a ese trabajador");
        workspace.EstaAbierto.Should().BeTrue("no cayó nada: no hay nada muerto que retirar");
    }

    /// <summary>
    /// FS-09 (auditoría UX de flujos sin salida, 2026-09-24): tras eliminar en lote no
    /// había salida salvo pedir a un Administrador del Tenant que recuperase uno a uno
    /// desde Auditoría. El aviso ofrece «Deshacer», que restaura solo los que el lote sí
    /// eliminó, y el diálogo lo anuncia.
    /// </summary>
    [Fact]
    public async Task Eliminar_en_lote_ofrece_deshacer_que_restaura_solo_los_que_cayeron()
    {
        var ana = Trabajador("Ana", "Moreno");
        var bea = Trabajador("Bea", "Alonso");
        var mediador = new MediatorFalso { Almacen = { bea, ana } };
        mediador.NoEliminables.Add(bea.Dto.Id);
        var cut = Renderizar(mediador);

        await AlternarSeleccionMultiple(cut);
        await cut.Find("tbody input[aria-label='Seleccionar a Ana Moreno']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.Find("tbody input[aria-label='Seleccionar a Bea Alonso']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados").ClickAsync(new MouseEventArgs());
        cut.Find("[role=dialog]").TextContent.Should().Contain("Podrás deshacer la eliminación desde el aviso que aparecerá, pero las asignaciones seguirán de baja");
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Alonso"));

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Single(m => m.TextoAccion == "Deshacer");
        await cut.InvokeAsync(aviso.OnAccion!);

        mediador.Enviadas.OfType<RestaurarTrabajadorCommand>().Should().Equal([new RestaurarTrabajadorCommand(ana.Dto.Id)],
            "se restaura solo lo que el lote eliminó, no el superviviente");
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Alonso", "Moreno"));
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(m => m.Mensaje == "1 trabajador(es) restaurado(s).");
    }

    /// <summary>Con los ids del lote, la ficha de un superviviente sigue abierta (antes se retiraban todas las pedidas).</summary>
    [Fact]
    public async Task Eliminar_en_lote_no_retira_la_ficha_de_un_superviviente()
    {
        var ana = Trabajador("Ana", "Moreno");
        var bea = Trabajador("Bea", "Alonso");
        var mediador = new MediatorFalso { Almacen = { bea, ana } };
        mediador.NoEliminables.Add(bea.Dto.Id);
        var cut = Renderizar(mediador);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Trabajador, bea.Dto.Id, "Bea Alonso", "operacion"));

        await AlternarSeleccionMultiple(cut);
        await cut.Find("tbody input[aria-label='Seleccionar a Ana Moreno']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.Find("tbody input[aria-label='Seleccionar a Bea Alonso']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarTrabajadoresCommand>().Single().Ids.Should().BeEquivalentTo([ana.Dto.Id, bea.Dto.Id],
            "el caso solo vale si el superviviente iba en el lote");
        workspace.EstaAbierto.Should().BeTrue("Bea no cayó: su ficha no está muerta");
    }

    // --- P1-E2b: aviso de cambios sin guardar -------------------------------------------------

    /// <summary>Destino de salida distinto de la propia página: /trabajadores es el origen.</summary>
    private const string DestinoFueraDeTrabajadores = "/empresas";

    private async Task SalirYComprobarQuePreguntaAsync(IRenderedComponent<Trabajadores> cut)
    {
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var origen = navegacion.Uri;

        await cut.InvokeAsync(() => navegacion.NavigateTo(DestinoFueraDeTrabajadores));

        navegacion.Uri.Should().Be(origen, "con el formulario a medias la navegación se detiene");
        cut.FindAll(".modal-pie button").Should().Contain(b => b.TextContent.Trim() == "Salir y descartar");
    }

    private async Task SalirYComprobarQueNoPreguntaAsync(IRenderedComponent<Trabajadores> cut, string porque)
    {
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await cut.InvokeAsync(() => navegacion.NavigateTo(DestinoFueraDeTrabajadores));

        navegacion.Uri.Should().EndWith(DestinoFueraDeTrabajadores, porque);
        cut.FindAll(".modal-pie button").Should().NotContain(b => b.TextContent.Trim() == "Salir y descartar");
    }

    private static async Task AbrirAltaAsync(IRenderedComponent<Trabajadores> cut)
    {
        await cut.Find("header.cabecera-pagina .acciones-cabecera").QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "+ Nuevo trabajador").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
    }

    private static Task EscribirDocumentoAsync(IRenderedComponent<Trabajadores> cut, string documento) =>
        cut.FindComponents<CampoTexto>()
            .Single(c => c.Instance.Etiqueta == "Documento de identidad (DNI, NIE, TIE o pasaporte)")
            .Find("input").InputAsync(new ChangeEventArgs { Value = documento });

    [Fact]
    public async Task Aviso_salir_con_el_alta_de_trabajador_a_medias_pregunta()
    {
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);

        await EscribirDocumentoAsync(cut, "12345678Z");

        await SalirYComprobarQuePreguntaAsync(cut);
    }

    /// <summary>D-05 (recorrido de 2026-10-01): «Cancelar» con datos escritos cerraba sin avisar.</summary>
    [Fact]
    public async Task Cancelar_el_alta_con_datos_escritos_pregunta_si_descartar_y_seguir_editando_la_mantiene()
    {
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);
        await EscribirDocumentoAsync(cut, "12345678Z");

        await cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
        cut.FindAll(".drawer-panel").Should().NotBeEmpty("el formulario sigue abierto hasta que se confirme");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Seguir editando").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().NotBeEmpty();

        await cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().BeEmpty("confirmar el descarte cierra");
    }

    [Fact]
    public async Task Cancelar_el_alta_sin_tocar_cierra_sin_preguntar()
    {
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);

        await cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll("h2").Should().NotContain(h => h.TextContent.Trim() == "¿Descartar cambios?");
        cut.FindAll(".drawer-panel").Should().BeEmpty();
    }

    /// <summary>
    /// D-05: «Selecciona una empresa.» era del intento de guardar anterior; al elegir la empresa no puede
    /// seguir en pantalla hasta el siguiente guardado.
    /// </summary>
    [Fact]
    public async Task El_aviso_de_empresa_sin_elegir_desaparece_al_elegirla()
    {
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);
        await cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());
        cut.Find(".drawer-panel .alerta-formulario").TextContent.Trim().Should().Be("Selecciona una empresa.",
            "control positivo: el aviso existe antes de elegir");

        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = EmpresaEbro.ToString() });

        cut.FindAll(".drawer-panel .alerta-formulario").Should().BeEmpty();
    }

    [Fact]
    public async Task Aviso_el_alta_sin_tocar_con_la_Empresa_del_filtro_preseleccionada_no_pregunta()
    {
        var cut = Renderizar(new MediatorFalso());
        await ElegirEnLaPastilla(cut, "Empresa", "Montajes Ebro S.L.");
        await AbrirAltaAsync(cut);
        cut.Find(".drawer-panel select").GetAttribute("value").Should().Be(EmpresaEbro.ToString(),
            "el test necesita que la Empresa llegue preseleccionada desde el filtro");

        await SalirYComprobarQueNoPreguntaAsync(cut, "lo que la pantalla preselecciona no es un cambio de quien edita");
    }

    [Fact]
    public async Task Aviso_el_nombre_que_trae_la_URL_no_es_un_cambio()
    {
        var cut = Renderizar(new MediatorFalso(), "trabajadores?accion=crear&nombre=Javier");
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Javier",
            "el test necesita que el nombre llegue precargado por la URL");

        await SalirYComprobarQueNoPreguntaAsync(cut, "lo que trae la URL no es un cambio de quien edita");
    }

    [Fact]
    public async Task Aviso_guardar_el_alta_deja_salir_sin_preguntar()
    {
        var mediador = new MediatorFalso();
        var cut = Renderizar(mediador);
        await AbrirAltaAsync(cut);
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = EmpresaEbro.ToString() });
        await EscribirDocumentoAsync(cut, "12345678Z");

        await cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle("el caso solo vale si el alta se guardó");
        await SalirYComprobarQueNoPreguntaAsync(cut, "lo escrito ya está guardado");
    }

    [Fact]
    public async Task Aviso_asignar_a_centro_con_la_fecha_de_hoy_puesta_no_pregunta_y_al_cambiarla_si()
    {
        var bea = Trabajador("Bea", "Alonso");
        var cut = Renderizar(new MediatorFalso { Almacen = { bea } });
        await AbrirAsignarACentro(cut, bea);
        var fecha = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de alta");
        fecha.Instance.Valor.Should().NotBeNullOrEmpty("el test necesita la fecha de alta de hoy ya puesta");

        await fecha.Find("input").InputAsync(new ChangeEventArgs { Value = "2020-01-01" });

        await SalirYComprobarQuePreguntaAsync(cut);
    }

    [Fact]
    public async Task Aviso_asignar_a_centro_recien_abierto_no_pregunta()
    {
        var bea = Trabajador("Bea", "Alonso");
        var cut = Renderizar(new MediatorFalso { Almacen = { bea } });
        await AbrirAsignarACentro(cut, bea);
        cut.FindAll("[role=dialog]").Should().NotBeEmpty("el test necesita el diálogo abierto");

        await SalirYComprobarQueNoPreguntaAsync(cut, "la fecha de alta de hoy viene puesta: no es un cambio");
    }

    [Fact]
    public async Task Aviso_guardar_filtro_con_nombre_escrito_pregunta_y_cancelado_no()
    {
        // Con un filtro aplicado: sin ninguno, «Guardar filtro» está deshabilitado (no hay nada que guardar).
        var cut = Renderizar(new MediatorFalso(), "trabajadores?q=Salas");
        await PulsarEnMasFiltros(cut, "Guardar filtro");
        await cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre" && c.Instance.Placeholder != null)
            .Find("input").InputAsync(new ChangeEventArgs { Value = "Mis urgentes" });

        await SalirYComprobarQuePreguntaAsync(cut);
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Seguir editando").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=dialog] .modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        // D-05: «Cancelar» cierra como la X: con el nombre escrito pregunta, y descartar es lo que lo tira a propósito.
        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());

        await SalirYComprobarQueNoPreguntaAsync(cut, "cancelar descarta el nombre a propósito");
    }

    // ------------------------------------------------------------- «Guardar filtro» con ModalFormulario (S12, lote 3b)

    private static IElement GuardarDelFiltro(IRenderedComponent<Trabajadores> cut) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar");

    private async Task<IRenderedComponent<Trabajadores>> AbrirGuardarFiltroAsync(MediatorFalso mediador)
    {
        // Con un filtro aplicado: sin ninguno, «Guardar filtro» está deshabilitado (no hay nada que guardar).
        var cut = Renderizar(mediador, "trabajadores?q=Salas");
        await PulsarEnMasFiltros(cut, "Guardar filtro");
        return cut;
    }

    private static Task EscribirNombreDelFiltroAsync(IRenderedComponent<Trabajadores> cut, string nombre) =>
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre" && c.Instance.Placeholder != null)
            .Find("input").InputAsync(new ChangeEventArgs { Value = nombre });

    [Fact]
    public async Task Guardar_filtro_sin_nombre_esta_deshabilitado_y_dice_por_que_y_con_nombre_se_habilita()
    {
        var cut = await AbrirGuardarFiltroAsync(new MediatorFalso());

        GuardarDelFiltro(cut).HasAttribute("disabled").Should().BeTrue("sin nombre no hay nada que guardar");
        GuardarDelFiltro(cut).GetAttribute("title").Should().Be("Escribe un nombre para el filtro", "un primario deshabilitado sin motivo es el de D-02 y D-06");

        await EscribirNombreDelFiltroAsync(cut, "Mis urgentes");

        GuardarDelFiltro(cut).HasAttribute("disabled").Should().BeFalse();
        GuardarDelFiltro(cut).HasAttribute("title").Should().BeFalse("habilitado no hay motivo que decir");
    }

    [Fact]
    public async Task Guardar_filtro_con_nombre_envia_el_comando_y_cierra_el_modal()
    {
        var mediador = new MediatorFalso();
        var cut = await AbrirGuardarFiltroAsync(mediador);
        await EscribirNombreDelFiltroAsync(cut, "Mis urgentes");

        await GuardarDelFiltro(cut).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<GuardarFiltroCommand>().Should().ContainSingle().Which.Nombre.Should().Be("Mis urgentes");
        cut.FindAll("[role=dialog]").Should().BeEmpty("guardado el filtro, el modal se cierra");
    }

    /// <summary>
    /// Un filtro guardado con solo «Documentación» no se guarda vacío: el estado viaja en el JSON (antes «Guardar
    /// filtro» se activaba con el estado pero no lo guardaba, y salía un filtro sin nada).
    /// </summary>
    [Fact]
    public async Task Guardar_filtro_con_solo_Documentacion_guarda_el_estado()
    {
        var mediador = new MediatorFalso();
        var cut = Renderizar(mediador, "trabajadores?estado=Vencido");
        await PulsarEnMasFiltros(cut, "Guardar filtro");
        await EscribirNombreDelFiltroAsync(cut, "Vencidos");

        await GuardarDelFiltro(cut).ClickAsync(new MouseEventArgs());

        var valores = System.Text.Json.JsonDocument.Parse(mediador.Enviadas.OfType<GuardarFiltroCommand>().Single().ValoresJson).RootElement;
        valores.GetProperty("Estado").GetString().Should().Be(nameof(EstadoDocumento.Vencido));
    }

    /// <summary>Aplicar un filtro guardado con estado lo pone en la consulta y en la URL (?estado=), con su búsqueda.</summary>
    [Fact]
    public async Task Aplicar_un_filtro_guardado_con_estado_lo_aplica_y_lo_escribe_en_la_url()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz") },
            FiltrosGuardados = { new FiltroGuardadoDto(Guid.NewGuid(), "Vencidos de Vega", "{\"Busqueda\":\"Vega\",\"Estado\":\"Vencido\"}", DateTime.UtcNow) }
        };
        var cut = Renderizar(mediador);

        await PulsarEnMasFiltros(cut, "Vencidos de Vega");

        cut.WaitForAssertion(() => UltimaConsulta(mediador).EstadoDocumental.Should().Be(nameof(EstadoDocumento.Vencido)));
        UltimaConsulta(mediador).Busqueda.Should().Be("Vega");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Vega").And.Contain("estado=Vencido");
    }

    /// <summary>
    /// Un filtro guardado antes de que se guardara el estado (sin «Estado» en el JSON) se sigue leyendo, y como
    /// define el conjunto entero quita el estado que hubiera puesto.
    /// </summary>
    [Fact]
    public async Task Aplicar_un_filtro_guardado_sin_estado_quita_el_estado_puesto()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Trabajador("Javier", "Salas Moreno"), Trabajador("Ana", "Vega Ortiz") },
            FiltrosGuardados = { new FiltroGuardadoDto(Guid.NewGuid(), "Solo Vega", "{\"Busqueda\":\"Vega\"}", DateTime.UtcNow) }
        };
        var cut = Renderizar(mediador, "trabajadores?estado=Vigente");
        UltimaConsulta(mediador).EstadoDocumental.Should().Be(nameof(EstadoDocumento.Vigente), "control: el estado venía puesto");

        await PulsarEnMasFiltros(cut, "Solo Vega");

        cut.WaitForAssertion(() => UltimaConsulta(mediador).EstadoDocumental.Should().BeNull());
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Vega").And.NotContain("estado=");
    }

    [Fact]
    public async Task Un_rechazo_al_guardar_el_filtro_se_ve_en_el_aviso_fijo_del_modal_y_lo_escrito_no_se_pierde()
    {
        var mediador = new MediatorFalso { ResultadoGuardarFiltro = Result.Fallo<Guid>(Error.Crear("Filtro.NombreEnUso", "Ya tienes un filtro con ese nombre.")) };
        var cut = await AbrirGuardarFiltroAsync(mediador);
        await EscribirNombreDelFiltroAsync(cut, "Mis urgentes");

        await GuardarDelFiltro(cut).ClickAsync(new MouseEventArgs());

        cut.Find(".modal-aviso .alerta-formulario").TextContent.Should().Contain("Ya tienes un filtro con ese nombre.",
            "el mensaje del Result es el que el usuario ve, fuera del cuerpo desplazable y no en un toast que desaparece");
        cut.FindAll(".modal-cuerpo .alerta-formulario").Should().BeEmpty("el aviso va fuera del cuerpo desplazable (D-20)");
        cut.FindAll("[role=dialog]").Should().NotBeEmpty("el rechazo no cierra el modal ni tira lo escrito");

        await EscribirNombreDelFiltroAsync(cut, "Mis urgentes 2");

        cut.FindAll(".modal-aviso").Should().BeEmpty("escribir de nuevo retira el error anterior");
    }
}
