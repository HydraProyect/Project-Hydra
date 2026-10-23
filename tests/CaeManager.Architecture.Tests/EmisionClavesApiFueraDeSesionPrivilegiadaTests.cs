using CaeManager.Application.ApiKeys.Commands.GenerarClaveApi;
using CaeManager.Application.Common;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La emisión de claves de la API pública queda fuera de la Sesión Privilegiada hasta que exista
/// una operación privilegiada específica para ella (ver <see cref="GenerarClaveApiCommand"/>).
/// Lo que la mantiene fuera es <c>AutorizacionEscrituraBehavior</c>: con una sesión privilegiada
/// viva deniega todo <see cref="ICommandBase"/> salvo los marcados
/// <see cref="IComandoDeRestablecimientoSegundoFactor"/> o <see cref="IComandoDeAprovisionamiento"/>.
/// Marcar el comando con uno de ellos —o con <see cref="IComandoDeAutoservicio"/>, que relaja los
/// roles admitidos— lo metería dentro sin tocar el behavior; este test lo impide.
/// </summary>
public class EmisionClavesApiFueraDeSesionPrivilegiadaTests
{
    [Fact]
    public void GenerarClaveApiCommand_es_un_comando_que_el_behavior_evalua()
    {
        typeof(GenerarClaveApiCommand).Should().Implement<ICommandBase>(
            "si dejara de ser un comando, AutorizacionEscrituraBehavior lo dejaría pasar sin mirar la sesión");
    }

    [Fact]
    public void GenerarClaveApiCommand_no_lleva_ninguna_marca_que_lo_exima_en_Sesion_Privilegiada()
    {
        var interfaces = typeof(GenerarClaveApiCommand).GetInterfaces();

        interfaces.Should().NotContain(typeof(IComandoDeAprovisionamiento));
        interfaces.Should().NotContain(typeof(IComandoDeRestablecimientoSegundoFactor));
        interfaces.Should().NotContain(typeof(IComandoDeAutoservicio));

        // Cualquier marca futura del mismo tipo (IComandoDe…) en Application.Common también.
        interfaces.Where(i => i.Namespace == typeof(ICommandBase).Namespace && i.Name.StartsWith("IComandoDe", StringComparison.Ordinal))
            .Should().BeEmpty("una marca nueva que eximiera de la denegación necesita una decisión explícita");
    }
}
