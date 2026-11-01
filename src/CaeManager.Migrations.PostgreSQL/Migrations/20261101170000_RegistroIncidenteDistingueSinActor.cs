using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// <c>app_registrar_incidente_seguridad</c> deja de tratar «sin usuario en el contexto» como si el
    /// sujeto coincidiera con él (hallazgo 2 de la auditoría de SECURITY DEFINER, ASVS 4.0.3 V4.1.3 y
    /// V7.3.3). Con el código anterior, una señal sin usuario nacía sin marca y se agrupaba con las
    /// reales: plantada sobre el sujeto de otra persona absorbía su señal, y como la alerta solo sale de
    /// un incidente nuevo, la suprimía; y <c>es_nuevo</c> respondía si existía una señal agrupable.
    ///
    /// <para>
    /// <b>Sin contexto firmado</b> se rechaza con 42501: la aplicación siempre firma, salvo sin clave
    /// vigente, y entonces tampoco hay tráfico bajo RLS; un token inválido ya se rechazaba dentro de
    /// <c>app_contexto_validado</c>, y solo el vacío pasaba.
    /// </para>
    ///
    /// <para>
    /// <b>Con contexto firmado sin usuario</b> la detección sigue funcionando, y es legítima: el enlace
    /// «No he sido yo» llega por correo y sin sesión, y una señal nacida en un circuito de Blazor se
    /// registra en un ámbito nuevo que no tiene su identidad. Queda dicho lo que el contexto muestra:
    /// <c>sin_usuario_en_contexto</c> en la evidencia, Actor nulo en la transición y motivo que lo dice;
    /// no se deduce ni se inventa un Actor. Y nunca agrupa ni es agrupada: en la base una señal plantada y
    /// una legítima sin usuario son indistinguibles, así que cada una abre su incidente. No multiplica
    /// alertas: las señales con umbral saltan una vez por ventana en <c>VentanaDeslizantePorSujeto</c>, y
    /// «No he sido yo» es una confirmación humana.
    /// </para>
    ///
    /// <para>
    /// <b>Con usuario distinto del sujeto</b> solo agrupa con registros marcados por ese mismo usuario del
    /// contexto: otro no puede absorber su señal ni sondear si existe. Sujeto y usuario del contexto
    /// siguen en campos separados (Actor real y Usuario simulado). <b>Con usuario igual al sujeto</b> nada
    /// cambia.
    /// </para>
    ///
    /// <para>
    /// Las marcas son de la función: se retiran de la evidencia que llega, para que quien llama no
    /// atribuya el registro a otro usuario ni lo presente como sin usuario.
    /// </para>
    ///
    /// <para>
    /// <b>Reescritura por texto</b> sobre <c>pg_get_functiondef</c>, como
    /// <c>ReescrituraPoliticasContextoRls</c>: conserva firma, propietario, permisos, SECURITY DEFINER y
    /// <c>search_path</c>. Cada texto buscado ocupa una línea, porque el cuerpo guardado lleva el fin de
    /// línea de la compilación que lo creó, y debe aparecer exactamente una vez. <b>Ranura anterior:</b>
    /// firma y resultado no cambian, y la versión que sigue sirviendo durante el relevo ya firma cada
    /// conexión (<c>TenantRlsConnectionInterceptor</c>), así que basta un paso.
    /// </para>
    /// </summary>
    public partial class RegistroIncidenteDistingueSinActor : Migration
    {
        private const string Firma = "public.app_registrar_incidente_seguridad(uuid,uuid,text,text,uuid[],jsonb,integer)";

        /// <summary>Sustituciones en orden; el <c>Down</c> las deshace en orden inverso.</summary>
        public static readonly IReadOnlyList<(string Antes, string Despues)> Cambios =
        [
            Cambio("v_distinto boolean;", """
                v_distinto boolean;
                    v_sin_usuario boolean;
                """),
            Cambio("IF p_incidente_id IS NULL OR p_transicion_id IS NULL OR p_senal IS NULL OR p_sujeto IS NULL", """
                IF NOT public.app_ctx_valido() THEN
                        RAISE EXCEPTION 'Registro de incidente de seguridad sin contexto firmado' USING ERRCODE = '42501';
                    END IF;
                    IF p_incidente_id IS NULL OR p_transicion_id IS NULL OR p_senal IS NULL OR p_sujeto IS NULL
                """),
            Cambio("v_distinto := v_ctx_usuario IS NOT NULL AND p_sujeto <> 'usuario:' || v_ctx_usuario::text;", """
                v_distinto := v_ctx_usuario IS NOT NULL AND p_sujeto <> 'usuario:' || v_ctx_usuario::text;
                    v_sin_usuario := v_ctx_usuario IS NULL;
                """),
            Cambio("v_evidencia := p_evidencia;", """
                v_evidencia := p_evidencia - ARRAY['sujeto_distinto_del_contexto', 'contexto_usuario_id', 'sin_usuario_en_contexto', 'tenants_afectados_truncados']::text[];
                    IF v_sin_usuario THEN
                        v_evidencia := v_evidencia || pg_catalog.jsonb_build_object('sin_usuario_en_contexto', true);
                    END IF;
                """),
            Cambio("""AND (i."Evidencia" ? 'sujeto_distinto_del_contexto') = v_distinto""", """
                AND (i."Evidencia" ? 'sujeto_distinto_del_contexto') = v_distinto
                       AND (NOT v_distinto OR i."Evidencia" ->> 'contexto_usuario_id' = v_ctx_usuario::text)
                       AND NOT v_sin_usuario AND NOT (i."Evidencia" ? 'sin_usuario_en_contexto')
                """),
            Cambio("'Detectado por la señal ' || p_senal);", """
                'Detectado por la señal ' || p_senal || CASE WHEN v_sin_usuario THEN ' sin usuario en el contexto' ELSE '' END);
                """),
        ];

        private static (string Antes, string Despues) Cambio(string antes, string despues)
            => (antes, despues.ReplaceLineEndings("\n"));

        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql(Reescritura(Cambios));

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql(Reescritura(Enumerable.Reverse(Cambios).Select(c => (c.Despues, c.Antes))));

        private static string Reescritura(IEnumerable<(string Antes, string Despues)> cambios)
        {
            var pares = string.Join(",\n", cambios.Select(c => $"[$texto${c.Item1}$texto$, $texto${c.Item2}$texto$]"));
            return $$"""
                DO $migracion$
                DECLARE
                    funcion regprocedure := '{{Firma}}'::regprocedure;
                    definicion text;
                    par text[];
                    pares text[] := ARRAY[
                {{pares}}
                    ]::text[];
                BEGIN
                    IF NOT (SELECT prosecdef FROM pg_catalog.pg_proc WHERE oid = funcion) THEN
                        RAISE EXCEPTION 'La función % no es SECURITY DEFINER', funcion;
                    END IF;
                    definicion := pg_catalog.pg_get_functiondef(funcion);
                    FOREACH par SLICE 1 IN ARRAY pares LOOP
                        IF (length(definicion) - length(replace(definicion, par[1], ''))) / length(par[1]) <> 1 THEN
                            RAISE EXCEPTION 'No se puede reescribir de forma inequívoca la función %: %', funcion, par[1];
                        END IF;
                        definicion := replace(definicion, par[1], par[2]);
                    END LOOP;
                    EXECUTE definicion;
                END
                $migracion$;
                """.ReplaceLineEndings("\n");
        }
    }
}
