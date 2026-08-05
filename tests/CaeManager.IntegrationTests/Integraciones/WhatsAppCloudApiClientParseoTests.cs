using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Parseo del payload de webhook de WhatsApp Cloud API — los métodos
/// Extraer* son funciones puras sin llamada de red, así que se prueban con
/// el adaptador real (el único sitio que conoce el formato de wire de Meta).
/// </summary>
public class WhatsAppCloudApiClientParseoTests
{
    private static WhatsAppCloudApiClient CrearCliente() =>
        new(new HttpClient(), Options.Create(new WhatsAppCloudApiOptions()), NullLogger<WhatsAppCloudApiClient>.Instance);

    private const string PayloadTexto = """
    {
      "object": "whatsapp_business_account",
      "entry": [{
        "id": "waba-1",
        "changes": [{
          "value": {
            "messaging_product": "whatsapp",
            "metadata": { "display_phone_number": "34686543364", "phone_number_id": "111222333" },
            "contacts": [{ "profile": { "name": "Chris" }, "wa_id": "34600111222" }],
            "messages": [{
              "from": "34600111222",
              "id": "wamid.abc",
              "timestamp": "1754265600",
              "type": "text",
              "text": { "body": "Hola, necesito ayuda" }
            }]
          },
          "field": "messages"
        }]
      }]
    }
    """;

    private const string PayloadImagenYEstado = """
    {
      "object": "whatsapp_business_account",
      "entry": [{
        "id": "waba-1",
        "changes": [{
          "value": {
            "messaging_product": "whatsapp",
            "metadata": { "display_phone_number": "34686543364", "phone_number_id": "111222333" },
            "messages": [{
              "from": "34600111222",
              "id": "wamid.img",
              "timestamp": "1754265700",
              "type": "image",
              "image": { "id": "media-1", "mime_type": "image/jpeg", "caption": "La foto del parte" }
            }],
            "statuses": [{
              "id": "wamid.saliente",
              "status": "failed",
              "timestamp": "1754265800",
              "recipient_id": "34600111222",
              "errors": [{ "code": 131047, "title": "Re-engagement message" }]
            }]
          },
          "field": "messages"
        }]
      }]
    }
    """;

    [Fact]
    public void Extrae_los_phone_number_ids_del_payload()
    {
        CrearCliente().ExtraerPhoneNumberIds(PayloadTexto).Should().Equal("111222333");
    }

    [Fact]
    public void Un_payload_que_no_es_json_devuelve_lista_vacia()
    {
        CrearCliente().ExtraerPhoneNumberIds("esto no es json").Should().BeEmpty();
    }

    [Fact]
    public void Extrae_un_mensaje_de_texto_con_telefono_normalizado_y_nombre_de_perfil()
    {
        var notificacion = CrearCliente().ExtraerNotificacion(PayloadTexto, "111222333");

        notificacion.Mensajes.Should().ContainSingle();
        var mensaje = notificacion.Mensajes[0];
        mensaje.Wamid.Should().Be("wamid.abc");
        mensaje.Telefono.Should().Be("+34600111222");
        mensaje.NombrePerfil.Should().Be("Chris");
        mensaje.Tipo.Should().Be("text");
        mensaje.Texto.Should().Be("Hola, necesito ayuda");
        mensaje.FechaUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1754265600).UtcDateTime);
        notificacion.Estados.Should().BeEmpty();
    }

    [Fact]
    public void Extrae_imagen_con_media_id_y_status_failed_con_motivo()
    {
        var notificacion = CrearCliente().ExtraerNotificacion(PayloadImagenYEstado, "111222333");

        var mensaje = notificacion.Mensajes.Should().ContainSingle().Subject;
        mensaje.Tipo.Should().Be("image");
        mensaje.MediaId.Should().Be("media-1");
        mensaje.TipoContenido.Should().Be("image/jpeg");
        mensaje.Texto.Should().Be("La foto del parte");

        var estado = notificacion.Estados.Should().ContainSingle().Subject;
        estado.Wamid.Should().Be("wamid.saliente");
        estado.Estado.Should().Be("failed");
        estado.Error.Should().Be("Re-engagement message");
    }

    [Fact]
    public void Ignora_los_values_de_otras_lineas()
    {
        CrearCliente().ExtraerNotificacion(PayloadTexto, "999999999").Mensajes.Should().BeEmpty();
    }
}
