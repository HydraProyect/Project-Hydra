using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// La definición única de cumplimiento documental (decisiones del propietario, 2026-10-03): qué cuenta como «al día»
/// (numerador), qué entra en el denominador y de quién se mide cada porcentaje. Cada fila de la tabla de abajo fija un
/// eje que las cinco fórmulas anteriores resolvían distinto; una mutación de cualquiera de ellos pone una fila en rojo.
/// </summary>
public class CumplimientoDocumentalTests
{
    /// <summary>La tabla de entrada: el estado de un par exigido y si cuenta como al día.</summary>
    private static readonly (EstadoDocumento Estado, bool AlDia, string Motivo)[] Tabla =
    [
        (EstadoDocumento.Vigente, true, "vigente con margen"),
        (EstadoDocumento.Proximo, true, "Próximo sigue siendo válido hoy (decisión 2026-10-03, pregunta 1)"),
        (EstadoDocumento.Urgente, true, "Urgente sigue siendo válido hoy (decisión 2026-10-03, pregunta 1)"),
        (EstadoDocumento.SinCaducidad, true, "«No caduca» confirmado es al día y requerido, no queda fuera del universo"),
        (EstadoDocumento.SinConfirmar, false, "«Sin confirmar» no es al día en un porcentaje (decisión 2026-10-03, pregunta 2, opción a)"),
        (EstadoDocumento.Vencido, false, "un documento vencido no cumple"),
        (EstadoDocumento.Faltante, false, "lo que no existe no cumple"),
    ];

    [Theory]
    [InlineData(EstadoDocumento.Vigente)]
    [InlineData(EstadoDocumento.Proximo)]
    [InlineData(EstadoDocumento.Urgente)]
    [InlineData(EstadoDocumento.SinCaducidad)]
    [InlineData(EstadoDocumento.SinConfirmar)]
    [InlineData(EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.Faltante)]
    public void Cada_estado_cuenta_o_no_como_al_dia_segun_la_tabla(EstadoDocumento estado)
    {
        var fila = Tabla.Single(f => f.Estado == estado);

        CumplimientoDocumental.EsConforme(estado).Should().Be(fila.AlDia, fila.Motivo);
    }

    [Fact]
    public void La_tabla_cubre_todos_los_valores_del_enum()
    {
        // Un estado nuevo sin fila aquí cae en «no conforme» sin que nadie lo decida: este test obliga a decidirlo.
        Tabla.Select(f => f.Estado).Should().BeEquivalentTo(Enum.GetValues<EstadoDocumento>());
    }

    [Fact]
    public void Cada_par_exigido_entra_en_el_denominador_este_o_no_al_dia()
    {
        var fraccion = CumplimientoDocumental.Evaluar(Tabla.Select(f => f.Estado));

        fraccion.Requeridos.Should().Be(7, "los siete estados son siete pares exigidos: ninguno queda fuera del universo");
        fraccion.AlDia.Should().Be(4, "Vigente, Próximo, Urgente y Sin caducidad");
        fraccion.Porcentaje.Should().Be(57);
    }

    [Fact]
    public void El_caso_E1_del_expediente_da_tres_de_cuatro_con_el_sin_confirmar_en_el_denominador()
    {
        // A «No caduca», B vence a 200 días (Vigente), C vence a 20 días (Próximo), D «Sin confirmar»: 3 de 4.
        var fraccion = CumplimientoDocumental.Evaluar(
            [EstadoDocumento.SinCaducidad, EstadoDocumento.Vigente, EstadoDocumento.Proximo, EstadoDocumento.SinConfirmar]);

        (fraccion.AlDia, fraccion.Requeridos, fraccion.Porcentaje).Should().Be((3, 4, 75));
    }

    [Fact]
    public void Sin_ningun_requerido_no_hay_porcentaje_y_nunca_un_cien()
    {
        var fraccion = CumplimientoDocumental.Evaluar(Array.Empty<EstadoDocumento>());

        fraccion.Porcentaje.Should().BeNull("«sin requisitos» no es 0 % ni 100 %");
        fraccion.Should().Be(FraccionCumplimiento.SinRequisitos);
    }

    [Fact]
    public void Contar_por_recuentos_da_lo_mismo_que_contar_documento_a_documento()
    {
        var porRecuentos = CumplimientoDocumental.Evaluar(
        [
            (EstadoDocumento.Vigente, 5), (EstadoDocumento.Proximo, 2), (EstadoDocumento.Urgente, 1), (EstadoDocumento.SinCaducidad, 3),
            (EstadoDocumento.SinConfirmar, 4), (EstadoDocumento.Vencido, 6), (EstadoDocumento.Faltante, 0)
        ]);
        var documentoADocumento = CumplimientoDocumental.Evaluar(
            Enumerable.Repeat(EstadoDocumento.Vigente, 5)
                .Concat(Enumerable.Repeat(EstadoDocumento.Proximo, 2)).Concat([EstadoDocumento.Urgente])
                .Concat(Enumerable.Repeat(EstadoDocumento.SinCaducidad, 3))
                .Concat(Enumerable.Repeat(EstadoDocumento.SinConfirmar, 4))
                .Concat(Enumerable.Repeat(EstadoDocumento.Vencido, 6)));

        porRecuentos.Should().Be(documentoADocumento);
        porRecuentos.Should().Be(new FraccionCumplimiento(11, 21));
    }

    [Fact]
    public void El_porcentaje_redondea_al_entero_mas_cercano()
    {
        new FraccionCumplimiento(1, 3).Porcentaje.Should().Be(33);
        new FraccionCumplimiento(2, 3).Porcentaje.Should().Be(67);
        new FraccionCumplimiento(0, 5).Porcentaje.Should().Be(0, "medido y a cero: no es «sin datos»");
    }

    [Fact]
    public void Sumar_fracciones_suma_numeradores_y_denominadores_no_promedia_porcentajes()
    {
        // 1/1 (100 %) y 1/9 (11 %): la media de porcentajes daría 56 %; lo correcto, 2 de 10.
        var suma = FraccionCumplimiento.Sumar([new FraccionCumplimiento(1, 1), new FraccionCumplimiento(1, 9)]);

        suma.Should().Be(new FraccionCumplimiento(2, 10));
        suma.Porcentaje.Should().Be(20);
    }

    // ---------- Los cuatro contextos ----------

    private static readonly Guid CentroA = Guid.NewGuid();
    private static readonly Guid CentroB = Guid.NewGuid();
    private static readonly Guid ClienteX = Guid.NewGuid();
    private static readonly Guid ClienteY = Guid.NewGuid();
    private static readonly Guid EmpresaP = Guid.NewGuid();
    private static readonly Guid EmpresaQ = Guid.NewGuid();
    private static readonly Guid TrabajadorUno = Guid.NewGuid();
    private static readonly Guid TrabajadorDos = Guid.NewGuid();
    private static readonly Guid TrabajadorTres = Guid.NewGuid();
    private static readonly Guid TrabajadorSinEmpresa = Guid.NewGuid();

    private static ParDocumentalExigido Par(Guid centro, Guid cliente, Guid? empresa, Guid trabajador, EstadoDocumento estado) =>
        new(centro, cliente, empresa, trabajador, Guid.NewGuid(), estado);

    /// <summary>
    /// Centro A (Cliente X): Uno (Empresa P) tiene dos pares, uno al día y uno faltante; Dos (Empresa Q) uno vencido.
    /// Centro B (Cliente Y): Uno (Empresa P, también aquí) un par al día; Tres (Empresa P) uno sin confirmar;
    /// Sin empresa, uno al día.
    /// </summary>
    private static readonly ParDocumentalExigido[] Pares =
    [
        Par(CentroA, ClienteX, EmpresaP, TrabajadorUno, EstadoDocumento.Vigente),
        Par(CentroA, ClienteX, EmpresaP, TrabajadorUno, EstadoDocumento.Faltante),
        Par(CentroA, ClienteX, EmpresaQ, TrabajadorDos, EstadoDocumento.Vencido),
        Par(CentroB, ClienteY, EmpresaP, TrabajadorUno, EstadoDocumento.Proximo),
        Par(CentroB, ClienteY, EmpresaP, TrabajadorTres, EstadoDocumento.SinConfirmar),
        Par(CentroB, ClienteY, null, TrabajadorSinEmpresa, EstadoDocumento.SinCaducidad),
    ];

    [Theory]
    [InlineData(ContextoCumplimiento.Centro, "A", 1, 3)]
    [InlineData(ContextoCumplimiento.Centro, "B", 2, 3)]
    [InlineData(ContextoCumplimiento.ClienteEmpresarial, "X", 1, 3)]
    [InlineData(ContextoCumplimiento.ClienteEmpresarial, "Y", 2, 3)]
    [InlineData(ContextoCumplimiento.Empresa, "P", 2, 4)]
    [InlineData(ContextoCumplimiento.Empresa, "Q", 0, 1)]
    [InlineData(ContextoCumplimiento.Trabajador, "Uno", 2, 3)]
    [InlineData(ContextoCumplimiento.Trabajador, "Dos", 0, 1)]
    [InlineData(ContextoCumplimiento.Trabajador, "Tres", 0, 1)]
    public void Cada_contexto_mide_solo_los_pares_que_le_pertenecen(ContextoCumplimiento contexto, string quien, int alDia, int requeridos)
    {
        var id = (contexto, quien) switch
        {
            (ContextoCumplimiento.Centro, "A") => CentroA,
            (ContextoCumplimiento.Centro, "B") => CentroB,
            (ContextoCumplimiento.ClienteEmpresarial, "X") => ClienteX,
            (ContextoCumplimiento.ClienteEmpresarial, "Y") => ClienteY,
            (ContextoCumplimiento.Empresa, "P") => EmpresaP,
            (ContextoCumplimiento.Empresa, "Q") => EmpresaQ,
            (ContextoCumplimiento.Trabajador, "Uno") => TrabajadorUno,
            (ContextoCumplimiento.Trabajador, "Dos") => TrabajadorDos,
            (ContextoCumplimiento.Trabajador, "Tres") => TrabajadorTres,
            _ => throw new InvalidOperationException(quien)
        };

        CumplimientoDocumental.De(contexto, id, Pares).Should().Be(new FraccionCumplimiento(alDia, requeridos));
    }

    [Fact]
    public void Un_contexto_sin_pares_esta_en_cero_de_cero_y_sin_porcentaje()
    {
        CumplimientoDocumental.De(ContextoCumplimiento.Centro, Guid.NewGuid(), Pares).Should().Be(FraccionCumplimiento.SinRequisitos);
    }

    [Fact]
    public void Un_Trabajador_en_dos_Centros_cuenta_un_par_por_cada_Centro_que_lo_exige()
    {
        // Uno está en A (dos pares) y en B (uno): 3 pares exigidos, no 2 tipos.
        CumplimientoDocumental.De(ContextoCumplimiento.Trabajador, TrabajadorUno, Pares).Requeridos.Should().Be(3);
    }

    [Fact]
    public void La_Empresa_no_incluye_a_los_Trabajadores_de_otras_Empresas_que_comparten_Centro_con_ella()
    {
        // El Centro A mezcla a Uno (P) y Dos (Q): la Empresa P solo mide lo suyo, aunque el Centro entero esté peor.
        var empresaP = CumplimientoDocumental.De(ContextoCumplimiento.Empresa, EmpresaP, Pares.Where(p => p.CentroId == CentroA));

        empresaP.Should().Be(new FraccionCumplimiento(1, 2));
        CumplimientoDocumental.De(ContextoCumplimiento.Centro, CentroA, Pares).Should().Be(new FraccionCumplimiento(1, 3));
    }

    [Fact]
    public void Un_Trabajador_sin_Empresa_cuenta_para_su_Centro_y_su_Cliente_pero_no_para_ninguna_Empresa()
    {
        var porEmpresa = CumplimientoDocumental.PorContexto(ContextoCumplimiento.Empresa, Pares);

        porEmpresa.Keys.Should().BeEquivalentTo([EmpresaP, EmpresaQ]);
        CumplimientoDocumental.De(ContextoCumplimiento.Trabajador, TrabajadorSinEmpresa, Pares).Should().Be(new FraccionCumplimiento(1, 1));
        CumplimientoDocumental.De(ContextoCumplimiento.ClienteEmpresarial, ClienteY, Pares).Requeridos.Should().Be(3);
    }

    [Theory]
    [InlineData(ContextoCumplimiento.Centro)]
    [InlineData(ContextoCumplimiento.Trabajador)]
    [InlineData(ContextoCumplimiento.Empresa)]
    [InlineData(ContextoCumplimiento.ClienteEmpresarial)]
    public void Los_contextos_de_un_mismo_nivel_reparten_todos_los_pares_sin_perder_ni_duplicar_ninguno(ContextoCumplimiento contexto)
    {
        var porContexto = CumplimientoDocumental.PorContexto(contexto, Pares);
        var conClave = contexto == ContextoCumplimiento.Empresa ? Pares.Count(p => p.EmpresaId is not null) : Pares.Length;

        porContexto.Values.Sum(f => f.Requeridos).Should().Be(conClave,
            "cada par pertenece a un único Centro, Trabajador, Cliente empresarial y, si la tiene, Empresa");
        porContexto.Values.Sum(f => f.AlDia).Should().Be(
            Pares.Where(p => contexto != ContextoCumplimiento.Empresa || p.EmpresaId is not null).Count(p => CumplimientoDocumental.EsConforme(p.Estado)));
    }

    [Fact]
    public void Un_contexto_desconocido_se_rechaza()
    {
        var accion = () => CumplimientoDocumental.De((ContextoCumplimiento)99, Guid.NewGuid(), Pares);

        accion.Should().Throw<ArgumentOutOfRangeException>();
    }
}
