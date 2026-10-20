#!/usr/bin/env python3
"""Check the real workflow's event gate without credentials, SSH, or deployment."""

import ast
from pathlib import Path
import re
import unittest


def evaluate_gate(expression, ref, event, verify_only):
    """Evaluate only boolean literals/operators and equality after substituting synthetic Actions inputs."""
    expression = expression.replace('github.ref', repr(ref))
    expression = expression.replace('github.event_name', repr(event))
    expression = expression.replace('inputs.verify_only', repr(verify_only))
    expression = expression.replace('&&', ' and ').replace('||', ' or ')
    expression = re.sub(r'!(?!=)', ' not ', expression).strip()

    def visit(node):
        """Reject executable Python, unknown Actions contexts, and unsupported operators fail-closed."""
        if isinstance(node, ast.Constant) and type(node.value) in (str, bool, type(None)):
            return node.value
        if isinstance(node, ast.BoolOp) and isinstance(node.op, (ast.And, ast.Or)):
            values = [bool(visit(value)) for value in node.values]
            return all(values) if isinstance(node.op, ast.And) else any(values)
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.Not):
            return not visit(node.operand)
        if isinstance(node, ast.Compare) and len(node.ops) == 1 and isinstance(node.ops[0], ast.Eq):
            return visit(node.left) == visit(node.comparators[0])
        raise AssertionError('Unsupported workflow gate syntax: ' + type(node).__name__)

    return bool(visit(ast.parse(expression, mode='eval').body))


class DeploymentWorkflowTests(unittest.TestCase):
    """Main-only deployment follows successful verification; manual dry runs remain available."""

    @classmethod
    def setUpClass(cls):
        """Read the tracked workflow, never a copied test expression or production configuration."""
        cls.workflow = (Path(__file__).resolve().parents[1] / '.github/workflows/deploy-production.yml').read_text(encoding='utf-8')
        cls.deploy = cls.workflow.split('\n  deploy:\n', 1)[1]
        cls.gate = re.search(r'^    if: (.+)$', cls.deploy, re.MULTILINE).group(1)

    def test_event_matrix(self):
        """Exercise absent push inputs, manual verification, PRs, feature branches and unrelated events."""
        cases = [
            ('refs/heads/main', 'push', None, True),
            ('refs/heads/main', 'push', False, True),
            ('refs/heads/feature', 'push', None, False),
            ('refs/tags/v1', 'push', None, False),
            ('refs/heads/main', 'workflow_dispatch', False, True),
            ('refs/heads/main', 'workflow_dispatch', True, False),
            ('refs/heads/feature', 'workflow_dispatch', False, False),
            ('refs/pull/1/merge', 'pull_request', False, False),
            ('refs/heads/main', 'pull_request', False, False),
            ('refs/heads/main', 'schedule', False, False),
        ]
        for ref, event, verify_only, expected in cases:
            with self.subTest(ref=ref, event=event, verify_only=verify_only):
                self.assertEqual(expected, evaluate_gate(self.gate, ref, event, verify_only))

    def test_failed_verification_cannot_be_bypassed(self):
        """GitHub's needs/success semantics must prevent failed tests from reaching Production."""
        self.assertRegex(self.deploy, r'(?m)^    needs: verify$')
        self.assertNotIn('always()', self.gate)
        self.assertNotIn('continue-on-error:', self.workflow)
        self.assertIn('environment:\n      name: production', self.deploy)
        self.assertIn('artifact-ids: ${{ needs.verify.outputs.artifact_id }}', self.deploy)
        self.assertIn('ARCHIVE_SHA256: ${{ needs.verify.outputs.archive_sha256 }}', self.deploy)
        self.assertIn('cancel-in-progress: false', self.workflow)

    def test_manual_default_deploys_and_verification_remains_explicit(self):
        """The manual UI defaults to deployment, but verify_only=true still suppresses it."""
        self.assertRegex(self.workflow, r'(?s)verify_only:\n.*?type: boolean\n        default: false')
        self.assertIn("branches: ['**']", self.workflow)
        self.assertIn('  pull_request:', self.workflow)


if __name__ == '__main__':
    unittest.main()
