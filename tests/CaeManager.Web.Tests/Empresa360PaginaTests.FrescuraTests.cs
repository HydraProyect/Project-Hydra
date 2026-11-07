using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

public partial class Empresa360PaginaTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public async Task Frescura_conserva_paginas_y_filas_si_falla_la_relectura(int lecturaQueFalla, bool agrupada)
    {
        var circuito = await CircuitoFrescuraDePrueba.RegistrarAsync(this);
        var mediador = Registrar(Empresa());
        var cut = Renderizar(agrupada ? "?pestana=trabajadores&agrupar=oficio" : "?pestana=trabajadores");
        await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        var nombres = cut.FindAll(".fila-relacion-nombre").Select(e => e.TextContent).ToArray();
        nombres.Should().NotBeEmpty();
        var fallo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lecturas = 0;
        mediador.Retener = p =>
        {
            if (p is not ObtenerTrabajadoresQuery || ++lecturas != lecturaQueFalla) return null;
            iniciada.TrySetResult();
            return fallo.Task;
        };
        circuito.Publicar(2);
        await iniciada.Task.WaitAsync(CircuitoFrescuraDePrueba.EsperaPositiva);
        cut.FindAll(".fila-relacion-nombre").Select(e => e.TextContent).Should().Equal(nombres);
        fallo.SetException(new InvalidOperationException("Fallo de relectura provocado por el test."));
        await Task.Delay(CircuitoFrescuraDePrueba.EsperaNegativa);
        circuito.Cache.AvisosRecibidos.Should().Be(1);
        cut.FindAll(".fila-relacion-nombre").Select(e => e.TextContent).Should().Equal(nombres);
        cut.Markup.Should().NotContain("No pudimos cargar");
        cut.Markup.Should().NotContain("aria-busy=\"true\"");
        if (!agrupada)
        {
            var antes = mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Count();
            await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Anterior")).ClickAsync(new MouseEventArgs());
            await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
            mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().HaveCount(antes, "las dos páginas buenas siguen en caché");
            cut.FindAll(".fila-relacion-nombre").Select(e => e.TextContent).Should().Equal(nombres);
        }
    }

    [Fact]
    public async Task Frescura_sustituye_la_cache_cuando_la_relectura_acierta()
    {
        var circuito = await CircuitoFrescuraDePrueba.RegistrarAsync(this);
        var mediador = Registrar(Empresa());
        var cut = Renderizar("?pestana=trabajadores");
        await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        mediador.Trabajadores[EmpresaId] = [.. mediador.Trabajadores[EmpresaId].Select(t => t with { Apellidos = "Nuevo " + t.Apellidos })];

        circuito.Publicar(2);

        cut.WaitForAssertion(
            () => cut.FindAll(".fila-relacion-nombre").Should().OnlyContain(e => e.TextContent.Contains("Nuevo")),
            CircuitoFrescuraDePrueba.EsperaPositiva);
        await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Anterior")).ClickAsync(new MouseEventArgs());
        cut.FindAll(".fila-relacion-nombre").Should().OnlyContain(e => e.TextContent.Contains("Nuevo"), "la primera página sale de la caché nueva");
    }

    [Fact]
    public async Task Frescura_repinta_con_los_datos_nuevos_si_el_usuario_cambio_de_pagina_durante_la_relectura()
    {
        var circuito = await CircuitoFrescuraDePrueba.RegistrarAsync(this);
        var mediador = Registrar(Empresa());
        var cut = Renderizar("?pestana=trabajadores");
        await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        mediador.Trabajadores[EmpresaId] = [.. mediador.Trabajadores[EmpresaId].Select(t => t with { Apellidos = "Nuevo " + t.Apellidos })];
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lecturas = 0;
        mediador.Retener = p =>
        {
            if (p is not ObtenerTrabajadoresQuery || ++lecturas != 6) return null;
            iniciada.TrySetResult();
            return liberar.Task;
        };

        circuito.Publicar(2);
        await iniciada.Task.WaitAsync(CircuitoFrescuraDePrueba.EsperaPositiva);
        await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Anterior")).ClickAsync(new MouseEventArgs());
        liberar.SetResult();

        cut.WaitForAssertion(
            () => cut.FindAll(".fila-relacion-nombre").Should().OnlyContain(e => e.TextContent.Contains("Nuevo")),
            CircuitoFrescuraDePrueba.EsperaPositiva);
    }

    // ── Chip de estado documental y contador de Plataformas: relectura por aviso y corte por cambio de Empresa ──

    private (Guid OtraId, Func<Task> Cambiar) PrepararOtraEmpresa(IRenderedComponent<EmpresaDetalle> cut, MediatorFalso mediador)
    {
        var otraId = Guid.NewGuid();
        mediador.Detalles[otraId] = new EmpresaDetalleDto(
            otraId, "Montajes Ebro S.L.", "B-50.111.222", new DateTime(2024, 1, 10, 9, 0, 0, DateTimeKind.Utc),
            [], Guid.NewGuid(), "4321", "Metal de Zaragoza", false);
        mediador.Cumplimiento[otraId] = 90;
        mediador.Trabajadores[otraId] = [Trabajador(EstadoDocumento.Vigente, "Gil")];
        async Task Cambiar()
        {
            Navegacion.NavigateTo($"empresas/{otraId}");
            await cut.InvokeAsync(() => cut.Render(p => p.Add(x => x.EmpresaId, otraId)));
            cut.WaitForAssertion(() => cut.Find("h1").TextContent.Should().Contain("Montajes Ebro S.L."));
            cut.WaitForAssertion(() => cut.FindAll("[aria-busy=true]").Should().BeEmpty());
        }
        return (otraId, Cambiar);
    }

    [Fact]
    public async Task Frescura_un_aviso_de_cambio_relee_el_chip_de_estado_documental_de_la_cabecera()
    {
        var circuito = await CircuitoFrescuraDePrueba.RegistrarAsync(this);
        var mediador = Registrar(Empresa());
        mediador.FilasDelListado[EmpresaId] = FilaDelListado(EstadoDocumento.Faltante);
        var cut = Renderizar();
        cut.WaitForAssertion(() => ChipEstado(cut)!.TextContent.Trim().Should().Be("Pendiente"));
        var lecturas = mediador.Enviadas.OfType<ObtenerEmpresasQuery>().Count();

        // El documento que faltaba llega (en otro circuito): el estado materializado de la Empresa ya está al día.
        mediador.FilasDelListado[EmpresaId] = FilaDelListado(EstadoDocumento.Vigente);
        circuito.Publicar(2);

        cut.WaitForAssertion(() => ChipEstado(cut).Should().BeNull("lo correcto se dice sin pastilla"), CircuitoFrescuraDePrueba.EsperaPositiva);
        cut.Find(".cabecera-identidad-datos").TextContent.Should().Contain("Sin incidencias");
        var relecturas = mediador.Enviadas.OfType<ObtenerEmpresasQuery>().Skip(lecturas).ToList();
        relecturas.Should().ContainSingle("un aviso es una relectura del chip");
        relecturas[0].EmpresaId.Should().Be(EmpresaId);
        relecturas[0].ConRecuentosPorEstado.Should().BeTrue();
    }

    [Fact]
    public async Task Frescura_si_la_relectura_del_chip_falla_se_queda_el_chip_que_habia()
    {
        var circuito = await CircuitoFrescuraDePrueba.RegistrarAsync(this);
        var mediador = Registrar(Empresa());
        mediador.FilasDelListado[EmpresaId] = FilaDelListado(EstadoDocumento.Faltante);
        var cut = Renderizar();
        cut.WaitForAssertion(() => ChipEstado(cut)!.TextContent.Trim().Should().Be("Pendiente"));
        var lecturas = mediador.Enviadas.OfType<ObtenerEmpresasQuery>().Count();

        mediador.FallarListado = true;
        circuito.Publicar(2);

        cut.WaitForAssertion(
            () => mediador.Enviadas.OfType<ObtenerEmpresasQuery>().Count().Should().BeGreaterThan(lecturas), CircuitoFrescuraDePrueba.EsperaPositiva);
        await Task.Delay(CircuitoFrescuraDePrueba.EsperaNegativa);
        ChipEstado(cut)!.TextContent.Trim().Should().Be("Pendiente", "un fallo de relectura no borra un estado ya enseñado");
    }

    [Fact]
    public async Task Cambiar_de_Empresa_descarta_el_chip_que_la_anterior_tenia_en_vuelo()
    {
        var mediador = Registrar(Empresa());
        mediador.FilasDelListado[EmpresaId] = FilaDelListado(EstadoDocumento.Vencido);
        var puerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediador.Retener = p => p is ObtenerEmpresasQuery { EmpresaId: var id } && id == EmpresaId ? puerta.Task : null;
        var cut = Renderizar();
        mediador.Enviadas.OfType<ObtenerEmpresasQuery>().Should().ContainSingle("la consulta de A salió y espera");
        var (otraId, cambiar) = PrepararOtraEmpresa(cut, mediador);

        await cambiar();
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerAcreditacionesPorProveedorQuery>().Should().ContainSingle(q => q.SoloDocumentosDeEmpresaId == otraId));
        puerta.SetResult();
        await Task.Delay(CircuitoFrescuraDePrueba.EsperaNegativa);
        cut.Render(p => p.Add(x => x.EmpresaId, otraId)); // otro repintado: lo que quedara escrito de A saldría ahora

        cut.Find("h1").TextContent.Should().Contain("Montajes Ebro S.L.");
        ChipEstado(cut).Should().BeNull("el Vencido de A llegó tarde y B no tiene fila en el listado");
        cut.Find(".cabecera-identidad-datos").TextContent.Should().NotContain("Vencido");
    }

    [Fact]
    public async Task Cambiar_de_Empresa_descarta_el_contador_de_Plataformas_que_la_anterior_tenia_en_vuelo()
    {
        var mediador = Registrar(Empresa());
        mediador.Plataformas[EmpresaId] = PlataformasConIncidencias(pendientes: 3);
        var puerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediador.Retener = p => p is ObtenerAcreditacionesPorProveedorQuery { SoloDocumentosDeEmpresaId: var id } && id == EmpresaId ? puerta.Task : null;
        var cut = Renderizar();
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerAcreditacionesPorProveedorQuery>().Should().ContainSingle("la lectura de A salió y espera"));
        var (otraId, cambiar) = PrepararOtraEmpresa(cut, mediador);

        await cambiar();
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerAcreditacionesPorProveedorQuery>().Should().Contain(q => q.SoloDocumentosDeEmpresaId == otraId));
        puerta.SetResult();
        await Task.Delay(CircuitoFrescuraDePrueba.EsperaNegativa);
        cut.Render(p => p.Add(x => x.EmpresaId, otraId)); // otro repintado: lo que quedara escrito de A saldría ahora

        Pestana(cut, "Plataformas").TextContent.Trim().Should().Be("Plataformas", "las 3 incidencias eran de A, no de B");
    }

    [Fact]
    public async Task Cambiar_de_Empresa_corta_la_carga_en_segundo_plano_de_la_anterior_antes_de_pedir_sus_plataformas()
    {
        var mediador = Registrar(Empresa());
        var puerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediador.Retener = p => p is ObtenerEmpresasQuery { EmpresaId: var id } && id == EmpresaId ? puerta.Task : null;
        var cut = Renderizar();
        var (otraId, cambiar) = PrepararOtraEmpresa(cut, mediador);

        await cambiar();
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerAcreditacionesPorProveedorQuery>().Should().Contain(q => q.SoloDocumentosDeEmpresaId == otraId));
        puerta.SetResult();
        await Task.Delay(CircuitoFrescuraDePrueba.EsperaNegativa);

        // A estaba esperando su chip cuando la ficha pasó a B: al despertar no sigue con las Plataformas (que leería ya con el Id de B).
        mediador.Enviadas.OfType<ObtenerAcreditacionesPorProveedorQuery>().Should().ContainSingle("solo B pide sus Plataformas, y una vez");
    }
}
