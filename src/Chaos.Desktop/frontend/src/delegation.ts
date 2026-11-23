import type { DelegationDto, DelegationItemDto } from "#neoastra";

/** Which side of a delegation the person is on, as the service reports it. */
export type DelegationSide = "From" | "To";

export type DelegationStateName = "Pending" | "Accepted" | "Question" | "Declined" | "Done" | "Closed" | "Withdrawn";

/** The delegation states in plain words, from one side's point of view. */
export function describeDelegation(state: string, side: DelegationSide): string {
  switch (state) {
    case "Pending":
      return side === "To" ? "waiting for your answer" : "waiting for an answer";
    case "Accepted":
      return "accepted";
    case "Question":
      return side === "To" ? "you asked a question" : "asked a question";
    case "Declined":
      return "declined";
    case "Done":
      return side === "From" ? "waiting for your review" : "reported done";
    case "Closed":
      return "closed";
    case "Withdrawn":
      return side === "From" ? "taken back" : "taken back by the delegator";
    default:
      return state;
  }
}

/** The moves a person may make on a delegation in its current state, from one side. */
export function delegationMoves(state: string, side: DelegationSide): string[] {
  if (side === "To") {
    switch (state) {
      case "Pending":
        return ["Accept", "Question", "Decline"];
      case "Accepted":
        return ["Done", "Question"];
      case "Question":
        return ["Accept", "Decline"];
      default:
        return [];
    }
  }
  switch (state) {
    case "Pending":
    case "Accepted":
    case "Question":
      return ["Withdraw", "Nudge"];
    case "Done":
      return ["Close", "Reopen"];
    default:
      return [];
  }
}

/** True when the delegation still awaits someone: the recipient (Pending, Accepted, Question) or the delegator's review (Done). */
export function isDelegationActive(state: string): boolean {
  return state === "Pending" || state === "Accepted" || state === "Question" || state === "Done";
}

/** The nudges in the last 24 hours, as the service counts them for the limit of three. */
export function recentNudges(item: DelegationItemDto, now: number): number {
  return item.nudges.filter((nudge) => now - Date.parse(nudge.sentAtUtc) < 24 * 60 * 60 * 1000).length;
}

/** A short line for the delegator's side of a delegation: the brief's headline and due date. */
export function delegationHeadline(delegation: DelegationDto): string {
  const parts: string[] = [];
  if (delegation.dueDate) parts.push(`due ${delegation.dueDate}`);
  if (delegation.reviewRequired) parts.push("review needed");
  return parts.join(" · ");
}
