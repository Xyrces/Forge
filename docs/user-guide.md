# Using Forge

Choose your project in the top bar. Open **Intake** when you have something you want to build, or **Sprints** when you want to see what’s happening to work you’ve already approved.

[Project vision](#set-the-project-direction) · [Intake](#start-an-initiative) · [Sprints](#follow-a-sprint) · [Pull requests](#review-a-pull-request) · [Deployments](#promote-a-release) · [When work stops](#find-out-why-work-stopped)

## Set the project direction

The **Vision Board** holds the ideas you expect to outlast a sprint. Describe who the project is for, how it should feel to use, and which engineering or design choices matter to you.

Open a node on the map to read its wording, or start typing about the project as a whole. Forge highlights the nodes it proposes to change. Compare the wording and choose **Apply draft** when it says what you mean. **Source document** lets you read or edit the complete Markdown. Forge saves your edits with the selected project and uses them to guide future work.

## Start an initiative

1. Open **Intake** and choose **New idea**, or pick up a conversation from the project inbox. A useful opening tells Forge what someone should be able to do and how you’ll know it works.
2. Read the **Working brief** alongside the conversation. If a requirement is missing, say so in the chat. Once there’s another revision, **Changes** shows which passages changed. Open an individual change for the full wording.
3. Choose **Approve revision** when the scope and acceptance criteria are right. Approval sends that revision to planning. **Open specification** takes you from the approved brief to the resulting spec.

A suggestion from Forge can also become an initiative. Open it from Intake, discuss it, and approve it the same way. For a small, concrete follow-up that already has a clear scope, **Backlog → New task** sends it for technical grooming.

## Follow a sprint

Forge prepares the design and breaks the work into tasks. It checks prerequisites before selecting a sprint, so independent work can run together and dependent tasks wait their turn. An unrelated priority normally waits for the current sprint to finish. A newly discovered prerequisite can join the sprint after grooming if it blocks work already there.

Open **Sprints** to see the goal and task cards. Expand **Acceptance and delivery** for the checks on that sprint and the environment it will deploy to, when one is configured. Open a card for its current situation and next action. **History & runs** shows what happened between attempts. You can follow a new run on the same task when a reviewer asks for a correction.

## Review a pull request

Set a project’s usual approval policy in **Projects → Project overview → Pull request approvals**. You can choose Forge review, human review, or both, and name a particular human reviewer.

To review one task yourself during a sprint, use the human review action on its card or in **Task actions**. You can also add the `needs-human-review` label to the pull request. Engineering and tests continue while the hold is in place. When the PR is ready, submit your review on GitHub. If you request changes, Forge can send the task back for correction on the same PR. The corrected revision needs the required approvals before merge.

The task’s **Review** section shows the decision and the revision it covers. A green build can still be waiting on a person, an open review conversation, or a repository rule. The project’s approval policy and GitHub’s branch rules both apply. [GitHub review identities and approval rules](github-app-identities.md)

## Promote a release

Each project sets its sprint target and promotion routes under **Projects → Project overview → Deployment environments**. For Forge itself, successful verification in the UAT environment records the deployment and closes the sprint.

When you’re ready to ship further, open **Deployments**, select the successful release you want, choose an allowed destination, and confirm the commit and image. Staging or production receives that same tested build, even if more changes have since landed on the branch. [Deployment environments and rollback](deployment-environments.md)

## Find out why work stopped

| When you see… | Where to look |
| --- | --- |
| A task waiting on another task | Open the card, then **Flow** for its dependencies and history. A failed prerequisite stays open until someone resolves it. |
| A PR waiting to merge | Open the task’s **Review** section and the linked PR. Check the required reviewer, open threads, and CI on the latest revision. |
| A worker or reviewer that failed | Open **History & runs**, inspect the latest attempt, and use the task’s retry action after addressing the cause. **Triage** groups recurring failures. |
| A whole stage on hold | Open **Flow → Edit** to inspect workflow pauses. A hold on sprint assembly leaves work in the active sprint running. |
| A spending limit | Open **Now** for the spending ceiling and outstanding reservations. |

Use **Agents** for model selection and **Projects** for worker limits. **Skills** shows the instructions available to each role. Edit a repository-owned skill in the project’s `.kilo/skills/` directory, or a dashboard-owned skill on that page. The [runtime reference](runtime-reference.md) covers configuration, logs, recovery, and secrets.

[Back to the documentation index](README.md)
