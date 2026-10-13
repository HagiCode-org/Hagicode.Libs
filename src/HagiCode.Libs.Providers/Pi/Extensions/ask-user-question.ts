// Bundled with HagiCode.Libs.Providers and loaded with `pi -e` when a PiOptions.UserInputHandler is set.
// Registers `ask_user_question`, whose input schema is the one of Claude's AskUserQuestion tool. Pi's own dialogs cannot
// carry a header, option descriptions or a multi-select flag (`select` takes plain strings, `custom` is TUI only), so the
// whole question travels as JSON in the title of one `input` dialog and the answers come back as JSON in its value.
//
// Wire contract, version 1 (keep in step with PiUserInputRequest in PiUserInput.cs):
//   title: "hagicode:ask_user_question:v1\n" + JSON { toolCallId, questions }
//   value: JSON { answers: { "<question text>": "<label>" | "<label>, <label>" | "<free text>" } }
//   no value (cancelled by the host, or the run was aborted): the tool reports that the user declined.
import { Type } from "@earendil-works/pi-ai";
import { defineTool, type ExtensionAPI } from "@earendil-works/pi-coding-agent";

const WIRE_MARKER = "hagicode:ask_user_question:v1";

const parameters = Type.Object({
	questions: Type.Array(
		Type.Object({
			question: Type.String({
				description:
					"The complete question to ask the user. It should be clear, specific and end with a question mark.",
			}),
			header: Type.String({ description: "Very short label for the question, at most 12 characters." }),
			options: Type.Array(
				Type.Object({
					label: Type.String({ description: "The text of the choice, 1-5 words." }),
					description: Type.String({ description: "What the choice means or what happens when it is picked." }),
				}),
				{ minItems: 2, maxItems: 4, description: "The choices. The user can always type another answer instead." },
			),
			multiSelect: Type.Optional(
				Type.Boolean({ description: "Set to true to let the user pick more than one option. Defaults to false." }),
			),
		}),
		{ minItems: 1, maxItems: 4, description: "The questions to ask, one to four." },
	),
});

const askUserQuestion = defineTool({
	name: "ask_user_question",
	label: "Ask user",
	description:
		"Ask the user one to four multiple-choice questions and wait for the answers. Use it to clarify requirements, choose between approaches or confirm a decision. The user may pick an option or type another answer.",
	parameters,
	executionMode: "sequential",
	async execute(toolCallId, params, signal, _onUpdate, ctx) {
		if (!ctx.hasUI) {
			return {
				content: [{ type: "text", text: "ask_user_question is unavailable: no interactive host is attached." }],
				details: {},
				isError: true,
			};
		}

		// The signal ends the dialog when the run is aborted, so the tool never outlives its run.
		const value = await ctx.ui.input(
			`${WIRE_MARKER}\n${JSON.stringify({ toolCallId, questions: params.questions })}`,
			"answers as JSON",
			{ signal },
		);
		if (value === undefined) {
			return {
				content: [{ type: "text", text: "User declined to answer the questions." }],
				details: { declined: true },
			};
		}

		let answers: Record<string, string> | undefined;
		try {
			const parsed = JSON.parse(value);
			if (parsed && typeof parsed === "object" && parsed.answers && typeof parsed.answers === "object") {
				answers = parsed.answers;
			}
		} catch {
			// Not JSON: a host that answers with plain text is passed through below.
		}

		const text = answers
			? `User has answered your questions: ${Object.entries(answers)
					.map(([question, answer]) => `"${question}"="${answer}"`)
					.join(", ")}. You can now continue with the user's answers in mind.`
			: `User has answered your questions: ${value}. You can now continue with the user's answers in mind.`;
		return { content: [{ type: "text", text }], details: { answers: answers ?? value } };
	},
});

export default function (pi: ExtensionAPI) {
	pi.registerTool(askUserQuestion);
}
