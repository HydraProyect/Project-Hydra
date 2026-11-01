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
    /// una legítima sin usuario son indistinguibles, así que cada una abre su incidente. El coste está
    /// acotado: las señales con umbral saltan una vez por ventana de la señal y proceso en
    /// <c>VentanaDeslizantePorSujeto</c>, y cada «No he sido yo» es una confirmación humana.
    /// </para>
    ///
    /// <para>
    /// <b>Con usuario en el contexto</b>, igual o distinto del sujeto, la columna
    /// <c>ContextoUsuarioId</c> guarda ese usuario y solo agrupa con registros de ese mismo usuario: otro
    /// no puede absorber su señal ni sondear si existe. Sujeto y usuario del contexto siguen en campos
    /// separados (Actor real y Usuario simulado).
    /// </para>
    ///
    /// <para>
    /// <b>La columna, y no la evidencia, decide.</b> La función anterior guardaba lo que quien llama
    /// escribiera en la evidencia, así que ninguna clave de una fila anterior prueba nada: con la clave
    /// fabricada, una fila sin usuario suprimiría la alerta de una señal autenticada. La columna no se
    /// puede falsificar: la función anterior no la conocía, ningún rol de la aplicación tiene INSERT ni
    /// UPDATE sobre la tabla (<c>cae_app_runtime</c> solo lee, bajo la política de Plataforma, y los
    /// otros dos no tienen nada), y la función la rellena con <c>app_ctx_usuario_id()</c>, sin parámetro.
    /// <c>contexto_usuario_id</c> y <c>sin_usuario_en_contexto</c> siguen en la evidencia para quien la
    /// lee en Plataforma, sin efecto en la agrupación.
    /// </para>
    ///
    /// <para>
    /// <b>Registros anteriores a esta migración:</b> tienen la columna nula. Pudieron nacer con usuario
    /// igual al sujeto o sin usuario, y la base no los distingue (la transición nunca guardó Actor). Son
    /// de procedencia desconocida: no agrupan, y la señal siguiente abre su incidente y su alerta. No se
    /// reescriben ni se les deduce usuario. No hace falta una fecha de corte: la columna y la función
    /// cambian en la misma transacción, la función es una sola para las dos ranuras, y una llamada
    /// anterior todavía en curso solo puede dejar la columna nula, que no agrupa. El efecto dura lo que
    /// la ventana de cada señal, 24 h como mucho, y su coste es un incidente de más.
    /// </para>
    ///
    /// <para>
    /// Las marcas de la evidencia son de la función: se retiran de la que llega, para que quien llama no
    /// atribuya el registro a otro usuario ni lo presente como sin usuario.
    /// </para>
    ///
    /// <para>
    /// <b>Reescritura por texto</b> sobre <c>pg_get_functiondef</c>, como
    /// <c>ReescrituraPoliticasContextoRls</c>: conserva firma, propietario, permisos, SECURITY DEFINER y
    /// <c>search_path</c>. Cada texto buscado ocupa una línea, porque el cuerpo guardado lleva el fin de
    /// línea de la compilación que lo creó, y debe aparecer exactamente una vez. <b>Ranura anterior:</b>
    /// firma y resultado no cambian, la columna es nullable y sus lecturas no la nombran, y la versión
    /// que sigue sirviendo durante el relevo ya firma cada conexión
    /// (<c>TenantRlsConnectionInterceptor</c>), así que basta un paso.
    /// </para>
    ///
    /// <para>
    /// <b>El <c>Down</c> no vuelve al cuerpo original</b>, que tenía la vulnerabilidad: deshace solo lo
    /// que necesita la columna. Se conservan el rechazo sin contexto firmado y las marcas, y como sin la
    /// columna no queda nada de lo que fiarse, ninguna señal agrupa: cuesta incidentes de más, nunca una
    /// alerta perdida. Por eso el <c>Up</c> acepta los dos puntos de partida, y si encuentra
    /// <see cref="GuardiaDeContextoFirmado"/> aplica solo <see cref="CambiosDeColumna"/>.
    /// </para>
    /// </summary>
    public partial class RegistroIncidenteDistingueSinUsuario : Migration
    {
        private const string Firma = "public.app_registrar_incidente_seguridad(uuid,uuid,text,text,uuid[],jsonb,integer)";
        private const string Tabla = "IncidentesSeguridadSospechados";
        private const string Columna = "ContextoUsuarioId";
        private const string SinAgrupar = "AND false -- sin la columna ContextoUsuarioId no se agrupa";

        /// <summary>El texto que distingue el cuerpo que deja el <c>Down</c> del original.</summary>
        public const string GuardiaDeContextoFirmado = "IF NOT public.app_ctx_valido() THEN";

        /// <summary>Lo que sobrevive al <c>Down</c>, en orden.</summary>
        public static readonly IReadOnlyList<(string Antes, string Despues)> CambiosDeContexto =
        [
            Cambio("v_distinto boolean;", """
                v_distinto boolean;
                    v_sin_usuario boolean;
                """),
            Cambio("IF p_incidente_id IS NULL OR p_transicion_id IS NULL OR p_senal IS NULL OR p_sujeto IS NULL", $"""
                {GuardiaDeContextoFirmado}
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
                    ELSE
                        v_evidencia := v_evidencia || pg_catalog.jsonb_build_object('contexto_usuario_id', v_ctx_usuario::text);
                    END IF;
                """),
            Cambio("""AND (i."Evidencia" ? 'sujeto_distinto_del_contexto') = v_distinto""", SinAgrupar),
            Cambio("'Detectado por la señal ' || p_senal);", """
                'Detectado por la señal ' || p_senal || CASE WHEN v_sin_usuario THEN ' sin usuario en el contexto' ELSE '' END);
                """),
        ];

        /// <summary>
        /// Lo que necesita la columna, en orden; el <c>Down</c> deshace solo esto. Agrupar es una igualdad:
        /// sin usuario en el contexto, o con la columna nula, no coincide con nada.
        /// </summary>
        public static readonly IReadOnlyList<(string Antes, string Despues)> CambiosDeColumna =
        [
            Cambio(SinAgrupar, $"""AND i."{Columna}" = v_ctx_usuario"""),
            Cambio("\"ConfirmadoEnUtc\")", $"\"ConfirmadoEnUtc\", \"{Columna}\")"),
            Cambio("v_tenants, v_evidencia, 'Sospechado', NULL);", "v_tenants, v_evidencia, 'Sospechado', NULL, v_ctx_usuario);"),
        ];

        private static (string Antes, string Despues) Cambio(string antes, string despues)
            => (antes, despues.ReplaceLineEndings("\n"));

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(name: Columna, table: Tabla, type: "uuid", nullable: true);
            migrationBuilder.Sql(Reescritura($"""
                CASE WHEN pg_catalog.strpos(definicion, $texto${GuardiaDeContextoFirmado}$texto$) > 0
                    THEN {Pares(CambiosDeColumna)}
                    ELSE {Pares(CambiosDeContexto.Concat(CambiosDeColumna))}
                END
                """));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Reescritura(Pares(CambiosDeColumna.Reverse().Select(c => (c.Despues, c.Antes)))));
            migrationBuilder.DropColumn(name: Columna, table: Tabla);
        }

        private static string Pares(IEnumerable<(string Antes, string Despues)> cambios)
            => "ARRAY[\n" + string.Join(",\n", cambios.Select(c => $"[$texto${c.Antes}$texto$, $texto${c.Despues}$texto$]")) + "\n]::text[]";

        private static string Reescritura(string pares)
        {
            return $$"""
                DO $migracion$
                DECLARE
                    funcion regprocedure := '{{Firma}}'::regprocedure;
                    definicion text;
                    par text[];
                    pares text[];
                BEGIN
                    IF NOT (SELECT prosecdef FROM pg_catalog.pg_proc WHERE oid = funcion) THEN
                        RAISE EXCEPTION 'La función % no es SECURITY DEFINER', funcion;
                    END IF;
                    definicion := pg_catalog.pg_get_functiondef(funcion);
                    pares := {{pares}};
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
