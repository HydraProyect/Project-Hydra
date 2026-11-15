// Tipos sintéticos para los controles positivos de DependenciasEntreModulosTests (ADR-013 § 4.3, test 2). Viven en el
// ensamblado de tests, nunca en src. Cada módulo tiene un namespace propio que el registro de control añade como área
// (ver DependenciasEntreModulosTests.RegistroDeControl); cada arista prohibida se esconde en un sitio distinto para
// comprobar que el instrumento lee el IL y no solo las firmas: el cuerpo de un método, una lambda y un argumento
// genérico dentro de un cuerpo.
#pragma warning disable CS0169, CS0414, IDE0051, IDE0052, S1144, S4487, S3459, S1481

namespace CaeManager.Application.ControlDeAristasNucleo
{
    internal sealed class DestinoNucleo;

    /// <summary>Núcleo -> Outbound por herencia: la arista está en la firma.</summary>
    internal sealed class OrigenNucleoHaciaOutbound : ControlDeAristasOutbound.DestinoOutbound;

    /// <summary>Núcleo -> Inbound por un argumento genérico de un campo: la arista está en la firma.</summary>
    internal sealed class OrigenNucleoHaciaInbound
    {
        private readonly List<ControlDeAristasInbound.DestinoInbound> _campo = [];
    }

    /// <summary>Núcleo -> Facturación solo en el cuerpo de un método.</summary>
    internal sealed class OrigenNucleoHaciaFacturacion
    {
        public static void Cuerpo() => ControlDeAristasFacturacion.DestinoFacturacion.Llamar();
    }

    /// <summary>Núcleo -> Outbound solo por la restricción de un parámetro genérico.</summary>
    internal sealed class OrigenNucleoPorRestriccion<T>
        where T : ControlDeAristasOutbound.DestinoOutbound;

    /// <summary>Un origen limpio: solo usa su propio módulo y no es ninguna arista.</summary>
    internal sealed class OrigenNucleoLimpio
    {
        public static object Cuerpo() => new DestinoNucleo();
    }
}

namespace CaeManager.Application.ControlDeAristasOutbound
{
    internal class DestinoOutbound
    {
        public static void Llamar()
        {
        }
    }

    /// <summary>Outbound -> Inbound solo dentro de una lambda.</summary>
    internal sealed class OrigenOutboundHaciaInbound
    {
        private readonly Func<object> _fabrica = () => new ControlDeAristasInbound.DestinoInbound();
    }

    /// <summary>Outbound -> Facturación (el patrón del cuadro de dirección y el cierre mensual).</summary>
    internal sealed class OrigenOutboundHaciaFacturacion
    {
        public static object Cuerpo() => new ControlDeAristasFacturacion.DestinoFacturacion();
    }

    internal sealed class ExcepcionDeOutbound : Exception;

    /// <summary>Outbound -> Núcleo está permitido: no debe aparecer en ninguna lista prohibida.</summary>
    internal sealed class OrigenOutboundHaciaNucleo
    {
        public static object Cuerpo() => new ControlDeAristasNucleo.DestinoNucleo();
    }
}

namespace CaeManager.Application.ControlDeAristasInbound
{
    internal sealed class DestinoInbound;

    /// <summary>Inbound -> Outbound solo en el cuerpo de un método.</summary>
    internal sealed class OrigenInboundHaciaOutbound
    {
        public static void Cuerpo() => ControlDeAristasOutbound.DestinoOutbound.Llamar();
    }

    /// <summary>Inbound -> Outbound solo por el tipo de una cláusula catch.</summary>
    internal sealed class OrigenInboundHaciaOutboundPorCatch
    {
        public static void Cuerpo()
        {
            try
            {
                Environment.GetEnvironmentVariable("CONTROL");
            }
            catch (ControlDeAristasOutbound.ExcepcionDeOutbound)
            {
                throw;
            }
        }
    }

    /// <summary>Inbound -> Facturación en el cuerpo de un método (para que el par tenga su propio control).</summary>
    internal sealed class OrigenInboundHaciaFacturacion
    {
        public static void Cuerpo() => ControlDeAristasFacturacion.DestinoFacturacion.Llamar();
    }

    /// <summary>Inbound -> Núcleo está permitido: no debe aparecer en ninguna lista prohibida.</summary>
    internal sealed class OrigenInboundHaciaNucleo
    {
        public static object Cuerpo() => new ControlDeAristasNucleo.DestinoNucleo();
    }
}

namespace CaeManager.Application.ControlDeAristasFacturacion
{
    internal sealed class DestinoFacturacion
    {
        public static void Llamar()
        {
        }
    }

    /// <summary>Facturación -> Outbound como argumento genérico dentro de un cuerpo.</summary>
    internal sealed class OrigenFacturacionHaciaOutbound
    {
        public static int Cuerpo() => new List<ControlDeAristasOutbound.DestinoOutbound>().Count;
    }

    /// <summary>Facturación -> Outbound dentro de la máquina de estado de un método asíncrono.</summary>
    internal sealed class OrigenFacturacionHaciaOutboundAsincrono
    {
        public static async Task<int> Cuerpo()
        {
            await Task.Yield();
            return new List<ControlDeAristasOutbound.DestinoOutbound>().Count;
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class AtributoConTiposDeControl(params Type[] tipos) : Attribute
    {
        public Type[] Tipos { get; } = tipos;
    }

    /// <summary>Facturación -> Inbound solo en un typeof dentro de un array de argumentos de atributo.</summary>
    [AtributoConTiposDeControl(typeof(ControlDeAristasInbound.DestinoInbound), typeof(string))]
    internal sealed class OrigenFacturacionHaciaInboundPorAtributo;

    /// <summary>Facturación -> Inbound en la firma de un método.</summary>
    internal sealed class OrigenFacturacionHaciaInbound
    {
        public static void Cuerpo(ControlDeAristasInbound.DestinoInbound _)
        {
        }
    }

    /// <summary>Facturación -> Núcleo está permitido.</summary>
    internal sealed class OrigenFacturacionHaciaNucleo
    {
        public static object Cuerpo() => new ControlDeAristasNucleo.DestinoNucleo();
    }
}
