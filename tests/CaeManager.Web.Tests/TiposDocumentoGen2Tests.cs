using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.TiposDocumento.Commands.ActualizarDeteccionTrabajadoresGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarPerfilDocumentoOficialGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarVerificacionIaGlobal;
using CaeManager.Application.TiposDocumento.Commands.CrearTipoDocumento;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTipoDocumentoPorId;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using TiposDocumentoPagina = CaeManager.Web.Features.TiposDocumento.Pages.TiposDocumento;

namespace CaeManager.Web.Tests;

/// <summary>
/// Tipos de documento contra su mockup Gen 2 («Tipos Documento TALVEG.dc.html»).
/// Los estados vacíos por filtro los sigue probando
/// <see cref="TiposDocumentoVacioPorFiltroTests"/>; esto cubre lo que el
/// rediseño añadió o corrigió.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué consultas y comandos llegan al mediador
/// y con qué parámetros, qué se pinta con lo que vuelve, y qué pasa cuando las
/// respuestas llegan fuera de orden (<see cref="TaskCompletionSource{TResult}"/>).
/// El doble responde según sus parámetros, como los lectores reales: la lista,
/// por texto, cliente, empresa y centro con la regla de
/// <see cref="ResolucionTipoDocumentoCentro"/> (como
/// <c>ObtenerTiposDocumentoQueryHandler</c>); los centros del selector, por
/// alcance, cliente y empresa; el detalle, con las filas Incluido=true; y la
/// edición aplica a las filas el mismo diff que
/// <c>EditarTipoDocumentoCommandHandler</c>.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el aspecto (bUnit no evalúa CSS: que la casilla
/// parezca un interruptor no se prueba aquí), la autorización de la ruta y de
/// los comandos, ni que los handlers reales hagan lo que el doble imita: el
/// diff de filas y el filtro de la lista son copias de su comportamiento, y
/// se prueban de verdad en Application. La regla de «¿se pide en este
/// centro?» sí es la real: la página y el doble llaman a
/// <see cref="ResolucionTipoDocumentoCentro.Aplica"/>.
/// </para>
/// </summary>
public class TiposDocumentoGen2Tests : BunitContext
{
    /// <summary><see cref="Modal"/> y <see cref="Drawer"/> importan dialogo-foco.js.</summary>
    public TiposDocumentoGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid ClienteA = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid ClienteB = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");
    private static readonly Guid EmpresaA = Guid.Parse("e1e1e1e1-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaB = Guid.Parse("e2e2e2e2-0000-0000-0000-000000000002");
    private static readonly Guid EmpresaC = Guid.Parse("e3e3e3e3-0000-0000-0000-000000000003");
    private static readonly Guid CentroZaragoza = Guid.Parse("c1c1c1c1-0000-0000-0000-000000000001");
    private static readonly Guid CentroTudela = Guid.Parse("c2c2c2c2-0000-0000-0000-000000000002");
    private static readonly Guid CentroHuesca = Guid.Parse("c4c4c4c4-0000-0000-0000-000000000004");
    private static readonly Guid CentroBilbao = Guid.Parse("c5c5c5c5-0000-0000-0000-000000000005");

    /// <summary>Un centro que el selector no enseña a quien edita (fuera de su alcance).</summary>
    private static readonly Guid CentroFueraDeAlcance = Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003");

    // ---------------------------------------------------------------- dobles

    private sealed class MediadorControlado(Func<object, Task<object?>> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return (TResponse)(await responder(request))!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Enviados.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>Un centro del tenant, con la contraparte cliente y la empresa a las que pertenece.</summary>
    private sealed record CentroDelTenant(Guid Id, Guid ClienteId, Guid EmpresaId, string Nombre, string Cliente, string Empresa);

    /// <summary>
    /// El «servidor» del test. <see cref="Tipos"/> y <see cref="Filas"/> son lo
    /// persistido: un comando que el test deja pasar los cambia, y la siguiente
    /// consulta lo devuelve.
    /// </summary>
    private sealed class Escenario
    {
        public List<ClienteSelectorDto> Clientes { get; } =
            [new(ClienteA, "Refrielectric S.A."), new(ClienteB, "Grupo Arbeko")];

        public Dictionary<Guid, List<EmpresaSelectorDto>> EmpresasPorCliente { get; } = new()
        {
            [ClienteA] = [new(EmpresaA, "Montajes Ebro S.L."), new(EmpresaC, "Frío Industrial Aragón S.L.")],
            [ClienteB] = [new(EmpresaB, "Elecnor Instalaciones S.A.U.")]
        };

        /// <summary>Todos los centros del tenant, los vea o no quien edita.</summary>
        public List<CentroDelTenant> CentrosDelTenant { get; } =
        [
            new(CentroZaragoza, ClienteA, EmpresaA, "Planta Zaragoza", "Refrielectric S.A.", "Montajes Ebro S.L."),
            new(CentroTudela, ClienteA, EmpresaA, "Nave logística Tudela", "Refrielectric S.A.", "Montajes Ebro S.L."),
            new(CentroFueraDeAlcance, ClienteA, EmpresaA, "Obra Calatayud", "Refrielectric S.A.", "Montajes Ebro S.L."),
            new(CentroHuesca, ClienteA, EmpresaC, "Almacén Huesca", "Refrielectric S.A.", "Frío Industrial Aragón S.L."),
            new(CentroBilbao, ClienteB, EmpresaB, "Planta Bilbao", "Grupo Arbeko", "Elecnor Instalaciones S.A.U.")
        ];

        /// <summary>Los centros que el selector enseña a quien edita (su alcance).</summary>
        public HashSet<Guid> CentrosVisibles { get; } = [CentroZaragoza, CentroTudela, CentroHuesca, CentroBilbao];

        public List<TipoDocumentoListaDto> Tipos { get; } = [];

        /// <summary>Filas TipoDocumentoCentro persistidas, Incluido=true y Incluido=false.</summary>
        public List<TipoDocumentoCentro> Filas { get; } = [];

        /// <summary>Decide el desenlace de cada comando; el éxito se aplica a lo persistido.</summary>
        public Func<object, Result> Decidir { get; set; } = _ => Result.Exito();

        /// <summary>Si devuelve una tarea, esa petición se resuelve cuando el test lo diga.</summary>
        public Func<object, Task<object?>?> Interceptar { get; set; } = _ => null;

        public void Fila(Guid tipoId, Guid centroId, bool incluido = true) => Filas.Add(new TipoDocumentoCentro(tipoId, centroId, incluido));

        /// <summary>Las filas del tipo como (centro, Incluido), para comparar lo persistido.</summary>
        public IEnumerable<(Guid CentroId, bool Incluido)> FilasDe(Guid tipoId) =>
            Filas.Where(f => f.TipoDocumentoId == tipoId).Select(f => (f.CentroId, f.Incluido));

        public Task<object?> Responder(object peticion) =>
            Interceptar(peticion) ?? Task.FromResult<object?>(peticion switch
            {
                ObtenerClientesParaSelectorQuery => Clientes.ToList(),
                ObtenerEmpresasParaSelectorQuery q => q.ClienteId is { } c && EmpresasPorCliente.TryGetValue(c, out var empresas)
                    ? empresas.ToList()
                    : new List<EmpresaSelectorDto>(),
                ObtenerCentrosParaSelectorQuery q => CentrosParaSelector(q),
                ObtenerTiposDocumentoQuery q => Filtrar(q),
                ObtenerTipoDocumentoPorIdQuery q => Detalle(q.Id),
                ActualizarLecturaIaGlobalCommand c => Aplicar(c, c.TipoDocumentoId, t => t with { LecturaIaActiva = c.Activa }),
                ActualizarDeteccionTrabajadoresGlobalCommand c => Aplicar(c, c.TipoDocumentoId, t => t with { DeteccionTrabajadoresActiva = c.Activa }),
                ActualizarVerificacionIaGlobalCommand c => Aplicar(c, c.TipoDocumentoId, t => t with { VerificacionIaActiva = c.Activa }),
                ActualizarPerfilDocumentoOficialGlobalCommand c => Aplicar(c, c.TipoDocumentoId, t => t with { PerfilDocumentoOficial = c.Perfil }),
                EditarTipoDocumentoCommand c => Editar(c),
                CrearTipoDocumentoCommand c => Crear(c),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
            });

        /// <summary>Como <c>ObtenerCentrosParaSelectorQueryHandler</c>: alcance, cliente y empresa; por cliente y nombre.</summary>
        private List<CentroSelectorDto> CentrosParaSelector(ObtenerCentrosParaSelectorQuery q) =>
            CentrosDelTenant
                .Where(c => CentrosVisibles.Contains(c.Id))
                .Where(c => q.ClienteId is null || c.ClienteId == q.ClienteId)
                .Where(c => q.EmpresaId is null || c.EmpresaId == q.EmpresaId)
                .OrderBy(c => c.Cliente, StringComparer.Ordinal).ThenBy(c => c.Nombre, StringComparer.Ordinal)
                .Select(c => new CentroSelectorDto(c.Id, c.Nombre, c.Cliente, c.Empresa))
                .ToList();

        /// <summary>
        /// Como <c>ObtenerTiposDocumentoQueryHandler</c>: con cualquier filtro de
        /// ámbito, un tipo sale si aplica a ALGUNO de los centros del tenant que
        /// casan con los tres filtros a la vez, según la regla real.
        /// </summary>
        public List<TipoDocumentoListaDto> Filtrar(ObtenerTiposDocumentoQuery q)
        {
            IEnumerable<TipoDocumentoListaDto> resultado = Tipos;
            if (q.AmbitoAplicacion is { } ambito)
                resultado = resultado.Where(t => t.AmbitoAplicacion == ambito);
            if (!string.IsNullOrWhiteSpace(q.Texto))
                resultado = resultado.Where(t => t.Nombre.Contains(q.Texto.Trim(), StringComparison.OrdinalIgnoreCase)
                    || t.Aliases.Any(a => a.Contains(q.Texto.Trim(), StringComparison.OrdinalIgnoreCase)));

            if (q.ClienteId is not null || q.EmpresaId is not null || q.CentroId is not null)
            {
                var centroIds = CentrosDelTenant
                    .Where(c => q.CentroId is null || c.Id == q.CentroId)
                    .Where(c => q.EmpresaId is null || c.EmpresaId == q.EmpresaId)
                    .Where(c => q.ClienteId is null || c.ClienteId == q.ClienteId)
                    .Select(c => c.Id)
                    .ToList();
                var filasPorPar = Filas.ToDictionary(f => (f.TipoDocumentoId, f.CentroId));

                resultado = resultado.Where(t => centroIds.Any(centroId =>
                    ResolucionTipoDocumentoCentro.Aplica(filasPorPar, t.Id, centroId, t.Requerido == RequisitoDocumental.Si)));
            }

            return resultado.OrderBy(t => t.Orden).ToList();
        }

        /// <summary>Como <c>ObtenerTipoDocumentoPorIdQueryHandler</c>: las filas Incluido=true, y aparte las Incluido=false.</summary>
        private TipoDocumentoDetalleDto? Detalle(Guid id) =>
            Tipos.FirstOrDefault(t => t.Id == id) is { } t
                ? new TipoDocumentoDetalleDto(t.Id, t.Nombre, t.VigenciaMeses, t.AplicaVencimientoAutomatico, t.Orden, t.AmbitoAplicacion,
                    t.Requerido, t.Naturaleza, Notas: null, t.Descripcion, t.CriteriosValidacion, t.SeSolicitaA, t.Observaciones,
                    Filas.Where(f => f.TipoDocumentoId == id && f.Incluido).Select(f => f.CentroId).ToList(), t.Aliases,
                    Filas.Where(f => f.TipoDocumentoId == id && !f.Incluido).Select(f => f.CentroId).ToList())
                : null;

        /// <summary>
        /// El diff de <c>EditarTipoDocumentoCommandHandler</c>: borra las filas
        /// Incluido=true cuyo centro no llega y crea las de los centros nuevos —
        /// salvo que el centro nuevo ya tenga una fila Incluido=false (exclusión
        /// dada de alta desde Requisitos del Centro): esa fila es la misma
        /// (TenantId, TipoDocumentoId, CentroId) que el índice único protege, así
        /// que se convierte a Incluido=true en vez de duplicarla. Las Incluido=false
        /// de un centro que no se marca aquí no se tocan.
        /// </summary>
        private Result Editar(EditarTipoDocumentoCommand c)
        {
            var resultado = Aplicar(c, c.Id, t => t with { Nombre = c.Nombre, Requerido = c.Requerido });
            if (resultado.EsFallido)
                return resultado;

            var actuales = Filas.Where(f => f.TipoDocumentoId == c.Id && f.Incluido).ToList();
            var deseados = c.CentroIds.Distinct().ToHashSet();
            var nuevos = deseados.Except(actuales.Select(f => f.CentroId)).ToList();

            foreach (var fila in actuales.Where(f => !deseados.Contains(f.CentroId)))
                Filas.Remove(fila);

            foreach (var centroId in nuevos)
            {
                var filaExcluida = Filas.SingleOrDefault(f => f.TipoDocumentoId == c.Id && f.CentroId == centroId);
                if (filaExcluida is not null)
                {
                    Filas.Remove(filaExcluida);
                    Filas.Add(new TipoDocumentoCentro(c.Id, centroId));
                }
                else
                {
                    Filas.Add(new TipoDocumentoCentro(c.Id, centroId));
                }
            }

            return resultado;
        }

        /// <summary>Como <c>CrearTipoDocumentoCommandHandler</c>: las filas, solo para el ámbito Trabajador.</summary>
        private Result<Guid> Crear(CrearTipoDocumentoCommand c)
        {
            var decision = Decidir(c);
            if (decision.EsFallido)
                return Result.Fallo<Guid>(decision.Error);

            var id = Guid.NewGuid();
            Tipos.Add(new TipoDocumentoListaDto(id, c.Nombre, c.VigenciaMeses, c.AplicaVencimientoAutomatico, c.Orden, c.AmbitoAplicacion,
                c.Requerido, c.Naturaleza, c.Descripcion, c.CriteriosValidacion, c.SeSolicitaA, c.Observaciones,
                LecturaIaActiva: false, DeteccionTrabajadoresActiva: false, VerificacionIaActiva: false, PerfilDocumentoOficial.Ninguno,
                c.Aliases ?? []));
            if (c.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            {
                foreach (var centroId in c.CentroIds.Distinct())
                    Filas.Add(new TipoDocumentoCentro(id, centroId));
            }

            return Result.Exito(id);
        }

        private Result Aplicar(object comando, Guid tipoId, Func<TipoDocumentoListaDto, TipoDocumentoListaDto> cambio)
        {
            var resultado = Decidir(comando);
            if (resultado.EsExitoso)
            {
                var indice = Tipos.FindIndex(t => t.Id == tipoId);
                if (indice >= 0)
                    Tipos[indice] = cambio(Tipos[indice]);
            }

            return resultado;
        }
    }

    private static TipoDocumentoListaDto Tipo(
        string nombre, AmbitoAplicacion ambito = AmbitoAplicacion.Empresa, int orden = 1,
        RequisitoDocumental requerido = RequisitoDocumental.No, NaturalezaJuridica naturaleza = NaturalezaJuridica.RequisitoCliente,
        int? vigencia = 12, PerfilDocumentoOficial oficial = PerfilDocumentoOficial.Ninguno, IReadOnlyList<string>? aliases = null) =>
        new(Guid.NewGuid(), nombre, vigencia, vigencia is not null, orden, ambito, requerido, naturaleza,
            Descripcion: null, CriteriosValidacion: null, SeSolicitaA: null, Observaciones: null,
            LecturaIaActiva: false, DeteccionTrabajadoresActiva: false, VerificacionIaActiva: false, oficial, aliases ?? []);

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<TiposDocumentoPagina> Cut, MediadorControlado Mediador) Renderizar(Escenario escenario, bool integrada = false)
    {
        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();

        var cut = Render<TiposDocumentoPagina>(p => p.Add(x => x.IntegradaEnConfiguracion, integrada));
        return (cut, mediador);
    }

    private ToastMensaje UltimoToast() => Services.GetRequiredService<ToastService>().Mensajes.Last();

    private static IElement Fila(IRenderedComponent<TiposDocumentoPagina> cut, string nombre) =>
        cut.FindAll("table.tabla-tipos tbody tr").Single(f => Texto(f.QuerySelector(".nombre-tipo")!) == nombre);

    private static IEnumerable<string> NombresPintados(IRenderedComponent<TiposDocumentoPagina> cut) =>
        cut.FindAll("table.tabla-tipos .nombre-tipo").Select(Texto);

    private static IElement Interruptor(IRenderedComponent<TiposDocumentoPagina> cut, string campo, string nombre) =>
        cut.Find($"input[role=switch][aria-label='{campo} de {nombre}']");

    /// <summary>Todo el texto de la celda del interruptor —lo mismo que mira el E2E con ToHaveText—.</summary>
    private static string TextoInterruptor(IRenderedComponent<TiposDocumentoPagina> cut, string campo, string nombre) =>
        Texto(Interruptor(cut, campo, nombre).ParentElement!);

    private static Task CambiarInterruptor(IRenderedComponent<TiposDocumentoPagina> cut, string campo, string nombre) =>
        Interruptor(cut, campo, nombre).ChangeAsync(new ChangeEventArgs { Value = true });

    private static IElement SelectorDeFiltro(IRenderedComponent<TiposDocumentoPagina> cut, int posicion) =>
        cut.FindAll(".barra-filtros select")[posicion];

    private static Task ElegirCliente(IRenderedComponent<TiposDocumentoPagina> cut, Guid clienteId) =>
        SelectorDeFiltro(cut, 0).ChangeAsync(new ChangeEventArgs { Value = clienteId.ToString() });

    private static Task ElegirEmpresa(IRenderedComponent<TiposDocumentoPagina> cut, Guid empresaId) =>
        SelectorDeFiltro(cut, 1).ChangeAsync(new ChangeEventArgs { Value = empresaId.ToString() });

    private static Task ElegirCentro(IRenderedComponent<TiposDocumentoPagina> cut, Guid centroId) =>
        SelectorDeFiltro(cut, 2).ChangeAsync(new ChangeEventArgs { Value = centroId.ToString() });

    private static Task PulsarEditar(IRenderedComponent<TiposDocumentoPagina> cut, string nombre) =>
        cut.Find($"button[aria-label='Editar {nombre}']").ClickAsync(new MouseEventArgs());

    private static Task PulsarNuevoTipo(IRenderedComponent<TiposDocumentoPagina> cut) =>
        cut.FindAll(".acciones-cabecera button").Single(b => Texto(b) == "+ Nuevo tipo").ClickAsync(new MouseEventArgs());

    private static IElement BotonDelPie(IRenderedComponent<TiposDocumentoPagina> cut, string texto) =>
        cut.FindAll(".drawer-pie button").Single(b => Texto(b) == texto);

    private static IElement BotonDelDialogo(IRenderedComponent<TiposDocumentoPagina> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => Texto(b) == texto);

    private static Task PulsarGuardar(IRenderedComponent<TiposDocumentoPagina> cut) =>
        BotonDelPie(cut, "Guardar").ClickAsync(new MouseEventArgs());

    /// <summary>El texto entero del diálogo: lo que dice y nada más.</summary>
    private static string TextoDelDialogo(IRenderedComponent<TiposDocumentoPagina> cut) => Texto(cut.Find(".modal-cuerpo p"));

    private static Task MarcarCentro(IRenderedComponent<TiposDocumentoPagina> cut, string nombreCentro, bool marcado) =>
        cut.FindAll(".lista-centros label").Single(l => l.TextContent.Contains(nombreCentro))
            .QuerySelector("input")!.ChangeAsync(new ChangeEventArgs { Value = marcado });

    /// <summary>El control de formulario asociado a una etiqueta del Drawer (CampoTexto/CampoSelect usan label for=).</summary>
    private static IElement ControlDelDrawer(IRenderedComponent<TiposDocumentoPagina> cut, string etiqueta)
    {
        var label = cut.FindAll(".drawer-panel label[for]").Single(l => Texto(l) == etiqueta);
        return cut.Find($"#{label.GetAttribute("for")}");
    }

    /// <summary>CampoTexto rebota 300 ms: InputAsync espera a que el valor llegue a la página.</summary>
    private static Task Escribir(IRenderedComponent<TiposDocumentoPagina> cut, string etiqueta, string valor) =>
        ControlDelDrawer(cut, etiqueta).InputAsync(new ChangeEventArgs { Value = valor });

    private static Task Elegir(IRenderedComponent<TiposDocumentoPagina> cut, string etiqueta, string valor) =>
        ControlDelDrawer(cut, etiqueta).ChangeAsync(new ChangeEventArgs { Value = valor });

    private static string Texto(IElement elemento) => elemento.TextContent.Trim();

    // ---------------------------------------------------------------- tabla

    [Fact]
    public void La_fila_rotula_ambito_vigencia_y_exigencia_como_se_configuraron()
    {
        var escenario = new Escenario();
        escenario.Tipos.AddRange(
        [
            Tipo("Ficha técnica del vehículo", AmbitoAplicacion.Vehiculo, 1, RequisitoDocumental.Si, NaturalezaJuridica.RequisitoCliente, vigencia: null),
            Tipo("Relación nominal de trabajadores", AmbitoAplicacion.Empresa, 2, RequisitoDocumental.Si, NaturalezaJuridica.PracticaSector, vigencia: 1, aliases: ["RNT", "TC2"]),
            Tipo("Formación en PRL", AmbitoAplicacion.Trabajador, 3, RequisitoDocumental.Condicional, NaturalezaJuridica.ObligacionCondicionada),
            Tipo("Protocolo de acceso", AmbitoAplicacion.Cliente, 4, RequisitoDocumental.No, NaturalezaJuridica.Recomendacion, vigencia: null)
        ]);
        var (cut, _) = Renderizar(escenario);

        string[] Celdas(string nombre) => Fila(cut, nombre).QuerySelectorAll("td").Select(Texto).ToArray();

        Celdas("Ficha técnica del vehículo")[2].Should().Be("Vehículo", "antes salía el nombre del enum, «Vehiculo»");
        Celdas("Ficha técnica del vehículo")[3].Should().Be("—");
        Celdas("Relación nominal de trabajadores")[3].Should().Be("1 mes", "antes decía «1 meses»");
        Celdas("Formación en PRL")[3].Should().Be("12 meses");
        Texto(Fila(cut, "Relación nominal de trabajadores").QuerySelector(".alias-tipo")!).Should().Be("RNT, TC2");

        Celdas("Ficha técnica del vehículo")[5].Should().Be("Requisito de cliente");
        Celdas("Relación nominal de trabajadores")[5].Should().Be("Práctica del sector");
        Celdas("Formación en PRL")[5].Should().Be("Si aplica");
        Celdas("Protocolo de acceso")[5].Should().Be("Opcional");

        Celdas("Relación nominal de trabajadores")[7].Should().NotBe("No aplica", "detectar trabajadores es de los tipos de Empresa");
        Celdas("Formación en PRL")[7].Should().Be("No aplica");
        Celdas("Formación en PRL")[8].Should().NotBe("No aplica", "la verificación IA es de los tipos de Trabajador");
        Celdas("Relación nominal de trabajadores")[8].Should().Be("No aplica");
        Fila(cut, "Protocolo de acceso").QuerySelectorAll("select").Should().ContainSingle("el documento oficial es de Empresa o Cliente");
        Celdas("Formación en PRL")[9].Should().Be("No aplica");
    }

    /// <summary>
    /// TALVEG orienta, no impone: la pantalla no sabe qué exige ninguna norma,
    /// solo lo que alguien configuró. La barrera va delante —el formulario
    /// abierto con su vista previa— para que la ausencia no sea verde vacío.
    /// </summary>
    [Fact]
    public async Task Ningun_texto_afirma_una_obligacion_que_la_pantalla_no_puede_saber()
    {
        var escenario = new Escenario();
        escenario.Tipos.Add(Tipo("Evaluación de riesgos", AmbitoAplicacion.Empresa, 1, RequisitoDocumental.Si, NaturalezaJuridica.ObligacionLegal));
        var (cut, _) = Renderizar(escenario);

        await PulsarEditar(cut, "Evaluación de riesgos");

        cut.Find(".vista-previa-exigencia").TextContent.Should().Contain("Obligación legal", "es el valor que se configuró, dicho como tal");
        // La celda de Exigencia (sexta), no el primer badge de la fila, que es el de Vencimiento automático.
        Fila(cut, "Evaluación de riesgos").QuerySelectorAll("td")[5].QuerySelector(".badge")!.GetAttribute("title")
            .Should().Be("¿Se pide? Sí, siempre · ¿Con qué autoridad? Obligación legal. Es el valor general: un centro puede tener su propia configuración.");
        cut.Markup.Should().NotContainAny(["Obligatorio", "obligatorio", "exige la ley", "Una norma lo exige"]);
    }

    [Fact]
    public void Embebida_en_Configuracion_titula_con_h2_y_enlaza_el_nivel_2_sin_salir_del_hub()
    {
        var (cut, _) = Renderizar(new Escenario(), integrada: true);

        cut.Find(".contenido-panel-configuracion h2.titulo-panel-configuracion").TextContent.Trim().Should().Be("Tipos de documento");
        cut.FindAll("h1").Should().BeEmpty("el hub ya tiene su h1");
        cut.Find(".cabecera-pagina-descripcion a").GetAttribute("href").Should().Be("/configuracion/ia");
    }

    [Fact]
    public void Con_ruta_propia_el_titulo_es_h1_y_el_enlace_va_al_selector_de_lectura_ia()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.Find(".contenedor-pagina h1").TextContent.Trim().Should().Be("Tipos de documento");
        cut.Find(".cabecera-pagina-descripcion a").GetAttribute("href").Should().Be("/configuracion/lectura-ia");
    }

    // ---------------------------------------------------------------- cambios en línea

    [Fact]
    public async Task Un_interruptor_cambia_en_el_acto_sin_recargar_la_lista_y_dice_Guardado()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        var (cut, mediador) = Renderizar(escenario);
        var consultasAntes = mediador.Enviados.OfType<ObtenerTiposDocumentoQuery>().Count();

        await CambiarInterruptor(cut, "Lectura IA", "Aptitud médica");

        mediador.Enviados.OfType<ActualizarLecturaIaGlobalCommand>().Should().ContainSingle()
            .Which.Should().Be(new ActualizarLecturaIaGlobalCommand(tipo.Id, true));
        mediador.Enviados.OfType<ObtenerTiposDocumentoQuery>().Count().Should().Be(consultasAntes,
            "antes cada interruptor recargaba la lista entera y la tabla se cambiaba por el esqueleto");
        Interruptor(cut, "Lectura IA", "Aptitud médica").HasAttribute("checked").Should().BeTrue();
        TextoInterruptor(cut, "Lectura IA", "Aptitud médica").Should().Be("Guardado ✓");
    }

    [Fact]
    public async Task Si_el_comando_falla_el_interruptor_vuelve_y_el_aviso_dice_por_que()
    {
        var escenario = new Escenario
        {
            Decidir = c => c is ActualizarVerificacionIaGlobalCommand
                ? Result.Fallo(Error.Crear("LecturaIa.SoloAdministrador", "Solo Administrador puede cambiar este interruptor."))
                : Result.Exito()
        };
        escenario.Tipos.Add(Tipo("Aptitud médica", AmbitoAplicacion.Trabajador));
        var (cut, _) = Renderizar(escenario);

        await CambiarInterruptor(cut, "Verificación IA", "Aptitud médica");

        Interruptor(cut, "Verificación IA", "Aptitud médica").HasAttribute("checked").Should().BeFalse(
            "el cambio no se guardó: la casilla no puede quedarse diciendo lo contrario de lo persistido");
        TextoInterruptor(cut, "Verificación IA", "Aptitud médica").Should().Be("No guardado");
        var toast = UltimoToast();
        toast.Tono.Should().Be(TonoToast.Error);
        toast.Mensaje.Should().Be(
            "No se pudo guardar el cambio en «Aptitud médica». Solo Administrador puede cambiar este interruptor. Se ha restaurado el valor anterior.");
    }

    [Fact]
    public async Task Mientras_el_comando_viaja_dice_Guardando_y_un_segundo_cambio_se_ignora()
    {
        var comando = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Interceptar = p => p is ActualizarLecturaIaGlobalCommand ? comando.Task : null
        };
        escenario.Tipos.Add(Tipo("Aptitud médica", AmbitoAplicacion.Trabajador));
        var (cut, mediador) = Renderizar(escenario);

        // Sin await: el doble retiene el comando.
        var primero = CambiarInterruptor(cut, "Lectura IA", "Aptitud médica");

        TextoInterruptor(cut, "Lectura IA", "Aptitud médica").Should().Be("Guardando…",
            "«Activa» solo puede aparecer cuando el cambio ya está persistido: el E2E espera exactamente ese texto");
        // Tampoco se espera el segundo antes de comprobar: sin la guarda, su
        // comando lo retendría el mismo doble y el test se colgaría en vez de
        // ponerse rojo (medido: blame lo abortó a los 90 s con un resumen verde).
        var segundo = Interruptor(cut, "Lectura IA", "Aptitud médica").ChangeAsync(new ChangeEventArgs { Value = false });
        mediador.Enviados.OfType<ActualizarLecturaIaGlobalCommand>().Should().ContainSingle(
            "el segundo cambio llegó con el primero en vuelo y habría mandado el valor contrario");

        await cut.InvokeAsync(() => comando.SetResult(Result.Exito()));
        await Task.WhenAll(primero, segundo);

        Interruptor(cut, "Lectura IA", "Aptitud médica").HasAttribute("checked").Should().BeTrue();
        TextoInterruptor(cut, "Lectura IA", "Aptitud médica").Should().Be("Guardado ✓");
    }

    [Fact]
    public async Task Una_lista_que_llega_mientras_el_cambio_viaja_no_lo_deshace()
    {
        var comando = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Interceptar = p => p is ActualizarLecturaIaGlobalCommand ? comando.Task : null
        };
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        escenario.Fila(tipo.Id, CentroZaragoza);
        var (cut, _) = Renderizar(escenario);

        var cambio = CambiarInterruptor(cut, "Lectura IA", "Aptitud médica");
        // La lista vuelve con lo persistido en ese instante: todavía desactivada.
        await ElegirCliente(cut, ClienteA);

        escenario.Tipos[0] = escenario.Tipos[0] with { LecturaIaActiva = true };
        await cut.InvokeAsync(() => comando.SetResult(Result.Exito()));
        await cambio;

        Interruptor(cut, "Lectura IA", "Aptitud médica").HasAttribute("checked").Should().BeTrue(
            "el comando se confirmó después de pintarse esa lista: lo que se ve tiene que ser lo guardado");
    }

    [Fact]
    public async Task Una_lista_pedida_antes_de_confirmarse_el_cambio_y_que_llega_despues_se_vuelve_a_pedir()
    {
        var comando = new TaskCompletionSource<object?>();
        var listaVieja = new TaskCompletionSource<object?>();
        var consultasDelCliente = 0;
        var escenario = new Escenario();
        escenario.Interceptar = p => p switch
        {
            ActualizarLecturaIaGlobalCommand => comando.Task,
            ObtenerTiposDocumentoQuery q when q.ClienteId == ClienteA && ++consultasDelCliente == 1 => listaVieja.Task,
            _ => null
        };
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        escenario.Fila(tipo.Id, CentroZaragoza);
        var (cut, mediador) = Renderizar(escenario);

        var cambio = CambiarInterruptor(cut, "Lectura IA", "Aptitud médica");
        var filtro = ElegirCliente(cut, ClienteA);

        escenario.Tipos[0] = escenario.Tipos[0] with { LecturaIaActiva = true };
        await cut.InvokeAsync(() => comando.SetResult(Result.Exito()));
        await cambio;
        // La lista se leyó antes de guardarse el cambio y llega ahora, con el valor de antes.
        await cut.InvokeAsync(() => listaVieja.SetResult(new List<TipoDocumentoListaDto> { tipo }));
        await filtro;

        consultasDelCliente.Should().Be(2, "esa lista pudo leerse antes del cambio: se pide otra en vez de pintarla");
        mediador.Enviados.OfType<ObtenerTiposDocumentoQuery>().Last().ClienteId.Should().Be(ClienteA);
        Interruptor(cut, "Lectura IA", "Aptitud médica").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public async Task El_documento_oficial_que_no_se_guarda_vuelve_al_anterior()
    {
        var escenario = new Escenario
        {
            Decidir = c => c is ActualizarPerfilDocumentoOficialGlobalCommand
                ? Result.Fallo(Error.Crear("PerfilDocumentoOficial.SoloAdministrador", "Solo Administrador puede cambiar el perfil oficial."))
                : Result.Exito()
        };
        var tipo = Tipo("Relación nominal de trabajadores", AmbitoAplicacion.Empresa);
        escenario.Tipos.Add(tipo);
        var (cut, mediador) = Renderizar(escenario);

        await Fila(cut, "Relación nominal de trabajadores").QuerySelector("select")!
            .ChangeAsync(new ChangeEventArgs { Value = nameof(PerfilDocumentoOficial.Rnt) });

        mediador.Enviados.OfType<ActualizarPerfilDocumentoOficialGlobalCommand>().Should().ContainSingle()
            .Which.Should().Be(new ActualizarPerfilDocumentoOficialGlobalCommand(tipo.Id, PerfilDocumentoOficial.Rnt));
        Fila(cut, "Relación nominal de trabajadores").QuerySelector("select")!.GetAttribute("value")
            .Should().Be(nameof(PerfilDocumentoOficial.Ninguno));
        Texto(Fila(cut, "Relación nominal de trabajadores").QuerySelector(".celda-oficial .aviso-cambio")!).Should().Be("No guardado");
        UltimoToast().Mensaje.Should().Contain("Solo Administrador puede cambiar el perfil oficial.");
    }

    // ---------------------------------------------------------------- filtros

    /// <summary>
    /// Cliente → Empresa → Centro: cada paso manda su coordenada y la lista
    /// pintada es la que corresponde a los tres filtros a la vez. El doble
    /// responde según Empresa y Centro como el lector real; si los ignorara,
    /// las comprobaciones de cada paso verían tipos de otra empresa o de otro
    /// centro.
    /// </summary>
    [Fact]
    public async Task La_cascada_envia_empresa_y_centro_y_la_lista_pintada_respeta_cada_filtro()
    {
        var escenario = new Escenario();
        var enZaragoza = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador, orden: 1);
        var enTudela = Tipo("Registro de entrega de EPI", AmbitoAplicacion.Trabajador, orden: 2);
        var enHuesca = Tipo("Permiso de trabajo en cámaras frigoríficas", AmbitoAplicacion.Trabajador, orden: 3);
        var siempreSalvoTudela = Tipo("Formación en PRL", AmbitoAplicacion.Trabajador, orden: 4, requerido: RequisitoDocumental.Si);
        var enBilbao = Tipo("Protocolo de acceso a planta", AmbitoAplicacion.Trabajador, orden: 5);
        escenario.Tipos.AddRange([enZaragoza, enTudela, enHuesca, siempreSalvoTudela, enBilbao]);
        escenario.Fila(enZaragoza.Id, CentroZaragoza);
        escenario.Fila(enTudela.Id, CentroTudela);
        escenario.Fila(enHuesca.Id, CentroHuesca);
        escenario.Fila(siempreSalvoTudela.Id, CentroTudela, incluido: false);
        escenario.Fila(enBilbao.Id, CentroBilbao);
        var (cut, mediador) = Renderizar(escenario);

        await ElegirCliente(cut, ClienteA);

        NombresPintados(cut).Should().Equal(
            ["Aptitud médica", "Registro de entrega de EPI", "Permiso de trabajo en cámaras frigoríficas", "Formación en PRL"],
            "el de Bilbao es de otro cliente");

        await ElegirEmpresa(cut, EmpresaA);

        mediador.Enviados.OfType<ObtenerCentrosParaSelectorQuery>().Last().Should().Be(new ObtenerCentrosParaSelectorQuery(ClienteA, EmpresaA));
        SelectorDeFiltro(cut, 2).QuerySelectorAll("option").Select(Texto).Should().Equal(
            ["Todos los centros", "Nave logística Tudela", "Planta Zaragoza"],
            "Almacén Huesca es de otra empresa y Obra Calatayud queda fuera del alcance de quien mira");
        mediador.Enviados.OfType<ObtenerTiposDocumentoQuery>().Last().Should().Be(new ObtenerTiposDocumentoQuery(ClienteA, EmpresaA));
        NombresPintados(cut).Should().Equal(
            ["Aptitud médica", "Registro de entrega de EPI", "Formación en PRL"],
            "el de Huesca solo se pide en un centro de otra empresa");

        await ElegirCentro(cut, CentroTudela);

        mediador.Enviados.OfType<ObtenerTiposDocumentoQuery>().Last().Should().Be(new ObtenerTiposDocumentoQuery(ClienteA, EmpresaA, CentroTudela));
        NombresPintados(cut).Should().Equal(
            ["Registro de entrega de EPI"],
            "«Aptitud médica» solo se pide en Zaragoza, y Tudela tiene «Formación en PRL» excluida en sus propios requisitos");
    }

    // ---------------------------------------------------------------- carreras de carga

    [Fact]
    public async Task La_lista_de_un_filtro_anterior_que_llega_tarde_se_descarta()
    {
        var listaA = new TaskCompletionSource<object?>();
        var escenario = new Escenario();
        var deA = Tipo("Seguro de responsabilidad civil", orden: 1);
        var deB = Tipo("Plan de seguridad y salud", AmbitoAplicacion.Proyecto, orden: 2);
        escenario.Tipos.AddRange([deA, deB]);
        escenario.Fila(deA.Id, CentroZaragoza);
        escenario.Fila(deB.Id, CentroBilbao);
        escenario.Interceptar = p => p is ObtenerTiposDocumentoQuery { ClienteId: var c } && c == ClienteA ? listaA.Task : null;
        var (cut, _) = Renderizar(escenario);

        var eleccionA = ElegirCliente(cut, ClienteA);
        await ElegirCliente(cut, ClienteB);
        await cut.InvokeAsync(() => listaA.SetResult(new List<TipoDocumentoListaDto> { deA }));
        await eleccionA;

        NombresPintados(cut).Should().Equal(["Plan de seguridad y salud"], "el cliente elegido es el B: la lista de A llegó tarde y no es suya");
    }

    [Fact]
    public async Task Las_empresas_de_un_cliente_anterior_que_llegan_tarde_no_rellenan_el_selector()
    {
        var empresasA = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Interceptar = p => p is ObtenerEmpresasParaSelectorQuery { ClienteId: var c } && c == ClienteA ? empresasA.Task : null
        };
        var (cut, _) = Renderizar(escenario);

        var eleccionA = ElegirCliente(cut, ClienteA);
        await ElegirCliente(cut, ClienteB);
        await cut.InvokeAsync(() => empresasA.SetResult(new List<EmpresaSelectorDto> { new(EmpresaA, "Montajes Ebro S.L.") }));
        await eleccionA;

        SelectorDeFiltro(cut, 1).QuerySelectorAll("option").Select(Texto)
            .Should().Equal("Todas las empresas", "Elecnor Instalaciones S.A.U.");
    }

    [Fact]
    public async Task Al_filtrar_la_tabla_se_queda_marcada_como_ocupada_en_vez_de_volver_el_esqueleto()
    {
        var lista = new TaskCompletionSource<object?>();
        var escenario = new Escenario();
        escenario.Tipos.Add(Tipo("Seguro de responsabilidad civil"));
        escenario.Interceptar = p => p is ObtenerTiposDocumentoQuery { ClienteId: not null } ? lista.Task : null;
        var (cut, _) = Renderizar(escenario);

        var eleccion = ElegirCliente(cut, ClienteA);

        cut.FindAll(".esqueleto-lista").Should().BeEmpty("el esqueleto es para cuando no hay nada que enseñar");
        cut.Find(".tabla-tipos-marco").GetAttribute("aria-busy").Should().Be("true");

        await cut.InvokeAsync(() => lista.SetResult(new List<TipoDocumentoListaDto>()));
        await eleccion;
    }

    [Fact]
    public async Task Si_la_carga_falla_reintentar_la_recupera()
    {
        var llamadas = 0;
        var escenario = new Escenario();
        escenario.Tipos.Add(Tipo("Seguro de responsabilidad civil"));
        escenario.Interceptar = p => p is ObtenerTiposDocumentoQuery && ++llamadas == 1
            ? Task.FromException<object?>(new InvalidOperationException("Base de datos caída (simulada)."))
            : null;
        var (cut, _) = Renderizar(escenario);

        cut.Find(".estado-vacio h3").TextContent.Trim().Should().Be("No pudimos cargar los tipos de documento");

        await cut.FindAll(".estado-vacio button").Single(b => Texto(b) == "Reintentar").ClickAsync(new MouseEventArgs());

        llamadas.Should().Be(2);
        NombresPintados(cut).Should().Equal("Seguro de responsabilidad civil");
    }

    // ---------------------------------------------------------------- formulario: ¿cambia lo que se pide?
    //
    // La confirmación salta si y solo si algún centro cambia según
    // ResolucionTipoDocumentoCentro.Aplica: la fila del par manda si existe;
    // si no, el valor general («¿Se pide?» == «Sí, siempre»).

    [Fact]
    public async Task Quitar_Si_siempre_pide_confirmacion_con_el_efecto_y_no_guarda_hasta_confirmar()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        escenario.Tipos.Add(tipo);
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Aptitud médica");
        await Elegir(cut, "¿Se pide?", nameof(RequisitoDocumental.No));
        await PulsarGuardar(cut);

        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().BeEmpty("todavía no se ha confirmado");
        TextoDelDialogo(cut).Should().Be(
            "«Aptitud médica» dejará de pedirse por defecto: los centros que no tengan su propia configuración para este tipo ya no lo pedirán.");

        await BotonDelDialogo(cut, "Guardar y aplicar").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().ContainSingle()
            .Which.Requerido.Should().Be(RequisitoDocumental.No);
        cut.FindAll(".drawer-panel").Should().BeEmpty();
        cut.FindAll(".modal-contenido").Should().BeEmpty();
    }

    /// <summary>
    /// Zaragoza ya lo pedía por su fila: no cambia y no se nombra. Cambian los
    /// centros sin fila propia, que ahora siguen «Sí, siempre».
    /// </summary>
    [Fact]
    public async Task Pasar_a_Si_siempre_pide_confirmacion_por_los_centros_sin_fila_propia()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        escenario.Fila(tipo.Id, CentroZaragoza);
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Aptitud médica");
        await Elegir(cut, "¿Se pide?", nameof(RequisitoDocumental.Si));
        await PulsarGuardar(cut);

        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().BeEmpty("todavía no se ha confirmado");
        TextoDelDialogo(cut).Should().Be(
            "«Aptitud médica» pasará a pedirse en todos los centros que no tengan su propia configuración para este tipo.");
    }

    [Fact]
    public async Task Crear_con_Si_siempre_y_cancelar_la_confirmacion_no_crea_nada_y_deja_el_formulario()
    {
        var (cut, mediador) = Renderizar(new Escenario());

        await PulsarNuevoTipo(cut);
        await Escribir(cut, "Nombre", "Certificado de formación en altura");
        await Elegir(cut, "¿Se pide?", nameof(RequisitoDocumental.Si));
        await PulsarGuardar(cut);

        TextoDelDialogo(cut).Should().Be(
            "«Certificado de formación en altura» se pedirá en todos los centros, porque «¿Se pide?» es «Sí, siempre».");

        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<CrearTipoDocumentoCommand>().Should().BeEmpty();
        cut.FindAll(".modal-contenido").Should().BeEmpty();
        cut.FindAll(".drawer-panel").Should().ContainSingle("cancelar la confirmación no descarta lo escrito");
    }

    /// <summary>
    /// Hallazgo de la revisión: con «No» o «Solo si aplica», marcar un centro
    /// crea una fila Incluido=true y ese centro pasa a pedirlo. Antes se
    /// guardaba sin preguntar.
    /// </summary>
    [Theory]
    [InlineData(RequisitoDocumental.No)]
    [InlineData(RequisitoDocumental.Condicional)]
    public async Task Crear_sin_Si_siempre_con_centros_marcados_pide_confirmacion_y_los_nombra(RequisitoDocumental requerido)
    {
        var escenario = new Escenario();
        var (cut, mediador) = Renderizar(escenario);

        await PulsarNuevoTipo(cut);
        await Escribir(cut, "Nombre", "Certificado de formación en altura");
        await Elegir(cut, "¿Se pide?", requerido.ToString());
        await MarcarCentro(cut, "Planta Zaragoza", true);
        await MarcarCentro(cut, "Nave logística Tudela", true);
        await PulsarGuardar(cut);

        mediador.Enviados.OfType<CrearTipoDocumentoCommand>().Should().BeEmpty("todavía no se ha confirmado");
        TextoDelDialogo(cut).Should().Be(
            "«Certificado de formación en altura» se pedirá en 2 centros marcados: Nave logística Tudela, Planta Zaragoza.");

        await BotonDelDialogo(cut, "Guardar y aplicar").ClickAsync(new MouseEventArgs());

        var creado = mediador.Enviados.OfType<CrearTipoDocumentoCommand>().Should().ContainSingle().Subject;
        creado.CentroIds.Should().BeEquivalentTo([CentroZaragoza, CentroTudela]);
        var nuevo = escenario.Tipos.Single(t => t.Nombre == "Certificado de formación en altura");
        escenario.FilasDe(nuevo.Id).Should().BeEquivalentTo([(CentroZaragoza, true), (CentroTudela, true)]);
    }

    [Fact]
    public async Task Editar_con_No_y_marcar_un_centro_nuevo_pide_confirmacion_y_lo_nombra()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        escenario.Fila(tipo.Id, CentroZaragoza);
        escenario.Fila(tipo.Id, CentroFueraDeAlcance);
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Aptitud médica");
        await MarcarCentro(cut, "Nave logística Tudela", true);
        await PulsarGuardar(cut);

        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().BeEmpty("todavía no se ha confirmado");
        TextoDelDialogo(cut).Should().Be("«Aptitud médica» se pedirá en 1 centro marcado: Nave logística Tudela.",
            "Zaragoza y Obra Calatayud ya lo pedían por su fila: solo cambia Tudela");

        await BotonDelDialogo(cut, "Guardar y aplicar").ClickAsync(new MouseEventArgs());

        escenario.FilasDe(tipo.Id).Should().BeEquivalentTo([(CentroZaragoza, true), (CentroFueraDeAlcance, true), (CentroTudela, true)]);
    }

    /// <summary>
    /// Defecto real: Tudela ya tenía una exclusión (Incluido=false, dada de alta desde
    /// Requisitos del Centro) para este mismo Tipo. Antes del fix, marcarla aquí y guardar
    /// intentaba crear una segunda fila para el par y el índice único
    /// (TenantId, TipoDocumentoId, CentroId) lo rechazaba con un 500 sin capturar.
    /// </summary>
    [Fact]
    public async Task Editar_con_No_y_marcar_un_centro_ya_excluido_lo_convierte_en_vez_de_duplicarlo()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        escenario.Fila(tipo.Id, CentroZaragoza);
        escenario.Fila(tipo.Id, CentroTudela, incluido: false);
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Aptitud médica");
        await MarcarCentro(cut, "Nave logística Tudela", true);
        await PulsarGuardar(cut);

        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().BeEmpty("todavía no se ha confirmado");
        TextoDelDialogo(cut).Should().Be("«Aptitud médica» se pedirá en 1 centro marcado: Nave logística Tudela.");

        await BotonDelDialogo(cut, "Guardar y aplicar").ClickAsync(new MouseEventArgs());

        escenario.FilasDe(tipo.Id).Should().BeEquivalentTo(
            [(CentroZaragoza, true), (CentroTudela, true)],
            "el índice único exige una sola fila por (Tipo, Centro): la exclusión se convierte, no se duplica");
    }

    /// <summary>
    /// Con «Sí, siempre» el valor general ya cubre a Tudela, pero su fila explícita
    /// (Incluido=false) lo excluía — sin verla, <c>CalcularCambio</c> daría "sin cambios"
    /// (Antes y Después caerían los dos al valor general) y guardaría sin avisar que
    /// Tudela pasa a pedirlo. <see cref="TipoDocumentoDetalleDto.CentroIdsExcluidos"/>
    /// existe para que esto no pase inadvertido.
    /// </summary>
    [Fact]
    public async Task Editar_con_Si_siempre_y_marcar_un_centro_ya_excluido_pide_confirmacion_y_lo_nombra()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Formación en PRL", AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        escenario.Tipos.Add(tipo);
        escenario.Fila(tipo.Id, CentroTudela, incluido: false);
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Formación en PRL");
        await MarcarCentro(cut, "Nave logística Tudela", true);
        await PulsarGuardar(cut);

        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().BeEmpty("todavía no se ha confirmado");
        TextoDelDialogo(cut).Should().Be("«Formación en PRL» se pedirá en 1 centro marcado: Nave logística Tudela.",
            "el valor general ya lo pedía, pero la fila explícita de Tudela lo excluía: sin verla, el diálogo no habría avisado del cambio real");

        await BotonDelDialogo(cut, "Guardar y aplicar").ClickAsync(new MouseEventArgs());

        escenario.FilasDe(tipo.Id).Should().BeEquivalentTo([(CentroTudela, true)]);
    }

    /// <summary>Un centro excluido que se deja sin marcar no debe entrar en el diálogo ni tocarse.</summary>
    [Fact]
    public async Task Un_centro_ya_excluido_que_no_se_marca_no_pide_confirmacion_ni_se_toca()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Formación en PRL", AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        escenario.Tipos.Add(tipo);
        escenario.Fila(tipo.Id, CentroTudela, incluido: false);
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Formación en PRL");
        await PulsarGuardar(cut);

        cut.FindAll(".modal-contenido").Should().BeEmpty("nada de lo que se pide cambia: Tudela sigue excluida");
        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().ContainSingle();
        escenario.FilasDe(tipo.Id).Should().BeEquivalentTo([(CentroTudela, false)]);
    }

    [Fact]
    public async Task Desmarcar_un_centro_con_No_pide_confirmacion_y_dice_cual_deja_de_pedirlo()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        escenario.Fila(tipo.Id, CentroZaragoza);
        escenario.Fila(tipo.Id, CentroFueraDeAlcance);
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Aptitud médica");
        await MarcarCentro(cut, "Planta Zaragoza", false);
        await PulsarGuardar(cut);

        TextoDelDialogo(cut).Should().Be(
            "«Aptitud médica» dejará de pedirse en 1 centro desmarcado: Planta Zaragoza, que pasa a seguir el valor general («No se pide»).");

        await BotonDelDialogo(cut, "Guardar y aplicar").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().ContainSingle()
            .Which.CentroIds.Should().BeEquivalentTo([CentroFueraDeAlcance]);
        escenario.FilasDe(tipo.Id).Should().BeEquivalentTo([(CentroFueraDeAlcance, true)]);
    }

    /// <summary>
    /// Hallazgo de la revisión: con «Sí, siempre», desmarcar un centro borra
    /// su fila Incluido=true y el centro pasa a seguir el valor general, que
    /// también lo pide. Nada cambia en lo que se pide: no hay que preguntar.
    /// </summary>
    [Fact]
    public async Task Desmarcar_un_centro_con_Si_siempre_no_pide_confirmacion()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        escenario.Tipos.Add(tipo);
        escenario.Fila(tipo.Id, CentroZaragoza);
        escenario.Fila(tipo.Id, CentroFueraDeAlcance);
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Aptitud médica");
        await MarcarCentro(cut, "Planta Zaragoza", false);
        await PulsarGuardar(cut);

        cut.FindAll(".modal-contenido").Should().BeEmpty("Zaragoza lo pedía por su fila y lo sigue pidiendo por el valor general");
        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().ContainSingle()
            .Which.CentroIds.Should().BeEquivalentTo([CentroFueraDeAlcance]);
        escenario.FilasDe(tipo.Id).Should().BeEquivalentTo([(CentroFueraDeAlcance, true)]);
    }

    /// <summary>
    /// Lectura → DTO → comando que borra por ausencia: un centro que el
    /// selector no enseña no se pudo desmarcar, así que no puede leerse como
    /// quitado. El doble aplica el diff del handler, así que lo que se
    /// comprueba es el efecto —las filas siguen—, no solo lo que viajó. Con
    /// «Sí, siempre» dejar fuera la fila invisible no cambiaría lo que se pide
    /// y no saltaría el diálogo: solo las filas lo delatan.
    /// </summary>
    [Theory]
    [InlineData(RequisitoDocumental.No)]
    [InlineData(RequisitoDocumental.Si)]
    public async Task Guardar_sin_cambios_no_pregunta_y_conserva_las_filas_que_no_se_ven(RequisitoDocumental requerido)
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador, requerido: requerido);
        escenario.Tipos.Add(tipo);
        escenario.Fila(tipo.Id, CentroZaragoza);
        escenario.Fila(tipo.Id, CentroFueraDeAlcance);
        // Una exclusión dada de alta desde los requisitos del centro: el formulario no la ve ni la toca.
        escenario.Fila(tipo.Id, CentroTudela, incluido: false);
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Aptitud médica");
        await PulsarGuardar(cut);

        cut.FindAll(".modal-contenido").Should().BeEmpty("nada de lo que se pide cambia");
        // Primero el efecto, después lo que viajó.
        escenario.FilasDe(tipo.Id).Should().BeEquivalentTo(
            [(CentroZaragoza, true), (CentroFueraDeAlcance, true), (CentroTudela, false)],
            "guardar sin cambios no puede borrar la fila de un centro que quien edita no veía");
        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().ContainSingle()
            .Which.CentroIds.Should().BeEquivalentTo([CentroZaragoza, CentroFueraDeAlcance]);
    }

    [Fact]
    public async Task Cambiar_solo_el_nombre_no_pide_confirmacion()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        escenario.Fila(tipo.Id, CentroZaragoza);
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Aptitud médica");
        await Escribir(cut, "Nombre", "Certificado de aptitud médica");
        await PulsarGuardar(cut);

        cut.FindAll(".modal-contenido").Should().BeEmpty("el nombre no cambia lo que se pide en ningún centro");
        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().ContainSingle()
            .Which.Nombre.Should().Be("Certificado de aptitud médica");
        NombresPintados(cut).Should().Equal("Certificado de aptitud médica");
    }

    // ---------------------------------------------------------------- formulario

    [Fact]
    public async Task El_resumen_de_centros_no_promete_que_sin_marcar_se_pide_en_todos()
    {
        var (cut, _) = Renderizar(new Escenario());

        await PulsarNuevoTipo(cut);

        Texto(cut.Find(".resumen-centros")).Should().Be(
            "Sin marcar ninguno, cada centro sigue el valor general («No se pide») y no lo pide por defecto.",
            "el mockup decía «vacío = aplica en todos», que con «No se pide» es falso");

        await Elegir(cut, "¿Se pide?", nameof(RequisitoDocumental.Si));

        Texto(cut.Find(".resumen-centros")).Should().StartWith("Sin marcar ninguno, cada centro sigue el valor general («Sí, siempre») y lo pide");
    }

    [Fact]
    public async Task Un_doble_clic_en_Guardar_envia_un_solo_comando()
    {
        var edicion = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Interceptar = p => p is EditarTipoDocumentoCommand ? edicion.Task : null
        };
        escenario.Tipos.Add(Tipo("Aptitud médica", AmbitoAplicacion.Trabajador));
        var (cut, mediador) = Renderizar(escenario);
        await PulsarEditar(cut, "Aptitud médica");

        // Ninguno de los dos se espera antes de comprobar: sin la guarda, el
        // segundo comando lo retendría el mismo doble y el test se colgaría.
        var primero = PulsarGuardar(cut);
        var segundo = PulsarGuardar(cut);

        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().ContainSingle();

        await cut.InvokeAsync(() => edicion.SetResult(Result.Exito()));
        await Task.WhenAll(primero, segundo);
    }

    [Fact]
    public async Task Pulsar_Editar_en_dos_filas_seguidas_abre_la_segunda()
    {
        var detalleA = new TaskCompletionSource<object?>();
        var escenario = new Escenario();
        var a = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador, orden: 1);
        var b = Tipo("Registro de entrega de EPI", AmbitoAplicacion.Trabajador, orden: 2);
        escenario.Tipos.AddRange([a, b]);
        escenario.Interceptar = p => p is ObtenerTipoDocumentoPorIdQuery q && q.Id == a.Id ? detalleA.Task : null;
        var (cut, _) = Renderizar(escenario);

        var aperturaA = PulsarEditar(cut, "Aptitud médica");
        await PulsarEditar(cut, "Registro de entrega de EPI");
        ControlDelDrawer(cut, "Nombre").GetAttribute("value").Should().Be("Registro de entrega de EPI");

        var detalleDeA = new TipoDocumentoDetalleDto(a.Id, a.Nombre, a.VigenciaMeses, a.AplicaVencimientoAutomatico, a.Orden,
            a.AmbitoAplicacion, a.Requerido, a.Naturaleza, null, null, null, null, null, [], [], []);
        await cut.InvokeAsync(() => detalleA.SetResult(detalleDeA));
        await aperturaA;

        ControlDelDrawer(cut, "Nombre").GetAttribute("value").Should().Be("Registro de entrega de EPI",
            "se pulsó Editar en la segunda fila: el detalle de la primera llegó tarde y no es el que se está editando");
    }

    [Fact]
    public async Task Crear_con_otro_ambito_no_envia_los_centros_marcados_para_Trabajador()
    {
        var (cut, mediador) = Renderizar(new Escenario());

        await PulsarNuevoTipo(cut);
        await Escribir(cut, "Nombre", "Póliza de responsabilidad civil");
        await MarcarCentro(cut, "Planta Zaragoza", true);
        await Elegir(cut, "Ámbito de aplicación", nameof(AmbitoAplicacion.Empresa));
        await PulsarGuardar(cut);

        cut.FindAll(".modal-contenido").Should().BeEmpty("sin centros que viajen y con «No se pide», ningún centro lo pide");
        var creado = mediador.Enviados.OfType<CrearTipoDocumentoCommand>().Should().ContainSingle().Subject;
        creado.AmbitoAplicacion.Should().Be(AmbitoAplicacion.Empresa);
        creado.CentroIds.Should().BeEmpty("los centros solo se eligen para el ámbito Trabajador y ya no se ven");
    }

    [Fact]
    public async Task Al_editar_el_ambito_se_ve_y_no_se_puede_cambiar()
    {
        var escenario = new Escenario();
        escenario.Tipos.Add(Tipo("Ficha técnica del vehículo", AmbitoAplicacion.Vehiculo));
        var (cut, _) = Renderizar(escenario);

        await PulsarEditar(cut, "Ficha técnica del vehículo");

        Texto(cut.Find(".ambito-fijo-valor")).Should().Be("Vehículo");
        cut.FindAll(".drawer-panel label[for]").Select(Texto).Should().NotContain("Ámbito de aplicación",
            "EditarTipoDocumentoCommand no lleva ámbito: un selector ahí prometería un cambio que no se guarda");
    }
}
